using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Files.Contracts;
using Iron.Kernel.Files.Data;
using Iron.Kernel.Files.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Files.Tests;

/// <summary>Runs without the database on purpose, so its message shows even when the fixture cannot migrate.</summary>
public class ModelTests
{
    [Fact]
    [Proves("ADR-0012")]
    public void TheModelHasNoChangesWithoutAMigration()
    {
        using var context = new FilesDbContext(DesignTime.OptionsFor<FilesDbContext>());

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The Files model has changes without a migration. Run: make ef-add MODULE=Files NAME=<WhatChanged>, then commit the files under src/Kernel/Iron.Kernel.Files/Data/Migrations.");
    }
}

[Collection(FilesCollection.Name)]
public class FileServiceTests(FilesFixture fixture)
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("Hello from the Files module. This is a small text file.");

    [Fact]
    [Proves("ADR-0008")]
    public async Task TheMigratedSchemaPassesTheSchemaCheck()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));

        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["schema-check"], TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("TF-160 AC1", "ADR-0016")]
    public async Task UploadRecordsTheHashAndDownloadVerifiesIt()
    {
        var uploader = Ids.New();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(uploader));
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<FileService>();

        var uploaded = await service.UploadAsync("notes.txt", "text/plain", Content.Length, new MemoryStream(Content), "widget", Ids.New(), TestContext.Current.CancellationToken);

        Assert.True(uploaded.IsSuccess, uploaded.IsFailure ? uploaded.Error.Message : null);
        var file = uploaded.Value;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Content)), file.Sha256);
        Assert.Equal(Content.Length, file.Size);
        Assert.Equal(uploader, file.CreatedBy);
        Assert.DoesNotContain("notes", file.StorageKey, StringComparison.Ordinal);

        var opened = await service.OpenAsync(file.Id, TestContext.Current.CancellationToken);
        Assert.True(opened.IsSuccess);
        using var copy = new MemoryStream();
        await using (opened.Value.Content)
        {
            await opened.Value.Content.CopyToAsync(copy, TestContext.Current.CancellationToken);
        }

        Assert.Equal(Content, copy.ToArray());
        Assert.True(await service.CompleteDownloadAsync(opened.Value, uploader, TestContext.Current.CancellationToken));

        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var events = await audit.Events.AsNoTracking().Where(e => e.RecordId == file.Id).Select(e => e.EventType).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(FilesAuditEvents.Uploaded, events);
        Assert.Contains(FilesAuditEvents.Downloaded, events);
    }

    [Fact]
    [Proves("TF-160 AC1", "ADR-0016")]
    public async Task ACorruptedObjectFailsTheIntegrityCheckAndIsAudited()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<FileService>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var file = (await service.UploadAsync("report.txt", "text/plain", Content.Length, new MemoryStream(Content), null, null, TestContext.Current.CancellationToken)).Value;

        // Something changed the bytes behind the row.
        await storage.PutAsync(file.StorageKey, new MemoryStream(Encoding.UTF8.GetBytes("Hello from the Files module. This is a small text file!")), "text/plain", TestContext.Current.CancellationToken);

        var opened = await service.OpenAsync(file.Id, TestContext.Current.CancellationToken);
        await using (opened.Value.Content)
        {
            await opened.Value.Content.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
        }

        Assert.False(await service.CompleteDownloadAsync(opened.Value, SystemPrincipal.SystemId, TestContext.Current.CancellationToken));
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.True(await audit.Events.AsNoTracking().AnyAsync(e => e.RecordId == file.Id && e.EventType == FilesAuditEvents.IntegrityFailed, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0016")]
    public async Task EmptyAndOversizedUploadsAreRefusedAndLeaveNothingBehind()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<FileService>();

        var empty = await service.UploadAsync("empty.txt", "text/plain", 0, new MemoryStream(), null, null, TestContext.Current.CancellationToken);
        Assert.Equal(FilesErrors.FileEmpty, empty.Error.Code);

        var declaredTooLarge = await service.UploadAsync("big.bin", "application/octet-stream", 2 * 1024 * 1024, new MemoryStream(Content), null, null, TestContext.Current.CancellationToken);
        Assert.Equal(FilesErrors.FileTooLarge, declaredTooLarge.Error.Code);

        var actuallyTooLarge = await service.UploadAsync("big.bin", "application/octet-stream", 10, new MemoryStream(new byte[1024 * 1024 + 1]), null, null, TestContext.Current.CancellationToken);
        Assert.Equal(FilesErrors.FileTooLarge, actuallyTooLarge.Error.Code);

        var context = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        Assert.False(await context.Files.AsNoTracking().AnyAsync(f => f.Name == "big.bin" || f.Name == "empty.txt", TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0008", "ADR-0016")]
    public async Task DeleteMarksTheRowAndHidesItFromReads()
    {
        var actor = Ids.New();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(actor));
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<FileService>();
        var file = (await service.UploadAsync("gone.txt", "text/plain", Content.Length, new MemoryStream(Content), null, null, TestContext.Current.CancellationToken)).Value;

        Assert.True((await service.DeleteAsync(file.Id, TestContext.Current.CancellationToken)).IsSuccess);

        Assert.Null(await service.FindAsync(file.Id, TestContext.Current.CancellationToken));
        var context = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        var row = await context.Files.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == file.Id, TestContext.Current.CancellationToken);
        Assert.True(row.IsDeleted);
        Assert.Equal(actor, row.DeletedBy);
        Assert.Equal(FilesErrors.FileNotFound, (await service.DeleteAsync(file.Id, TestContext.Current.CancellationToken)).Error.Code);
    }

    [Fact]
    [Proves("ADR-0016")]
    public async Task ReadinessNamesAMissingContainer()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), container: "nowhere");
        var check = new ObjectStorageHealthCheck(provider.GetRequiredService<IObjectStorage>());

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("The blob container 'nowhere' does not exist.", result.Description);

        await using var ready = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        var healthy = await new ObjectStorageHealthCheck(ready.GetRequiredService<IObjectStorage>()).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, healthy.Status);
    }

    [Fact]
    [Untraced("The message for object storage configured with neither a connection string nor a container URL is decided by no ADR or criterion; ADR-0016 decides such a message only for the secret store.")]
    public void StorageThatIsNotConfiguredSaysWhatIsMissing()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new BlobObjectStorage(new StorageOptions()));

        Assert.Contains("Storage:ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Untraced("That storage reached through a container signature checks for its container instead of creating it is an implementation guard no ADR or criterion states.")]
    public async Task ASignedStorageChecksItsContainerInsteadOfCreatingIt()
    {
        var account = new BlobContainerClient(fixture.StorageConnectionString, "signed-" + Guid.NewGuid().ToString("N")[..8]);
        var sas = account.GenerateSasUri(BlobContainerSasPermissions.All, TimeProvider.System.GetUtcNow().AddHours(1));
        var signed = new BlobObjectStorage(new StorageOptions { ContainerSasUrl = sas.ToString() });

        // Not created yet: the check fails instead of trying to create with a signature that cannot.
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => signed.EnsureContainerAsync(TestContext.Current.CancellationToken));
        Assert.Contains("does not exist", missing.Message, StringComparison.Ordinal);

        await account.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await signed.EnsureContainerAsync(TestContext.Current.CancellationToken);
        Assert.Null(await signed.CheckAsync(TestContext.Current.CancellationToken));
    }
}
