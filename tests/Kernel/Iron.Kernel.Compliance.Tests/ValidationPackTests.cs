using System.Text.Json;
using Iron.Kernel.Api.Context;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Compliance.Services;
using Iron.Kernel.Files.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Compliance.Tests;

/// <summary>
/// The validation pack of the version a tenant runs (S3 T8, TF-218 AC3, ADR-0052): the unit names its version, finds the
/// pack the fleet placed in its container for exactly that version, and audits its download.
/// </summary>
[Collection(EvidenceCollection.Name)]
public class ValidationPackTests(EvidenceFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-218 AC3", "ADR-0052")]
    public async Task TheUnitServesThePackOfExactlyTheVersionItRunsAndAuditsItsDownload()
    {
        var version = $"2031.04.{Random.Shared.Next(10, 28)}.{Random.Shared.Next(100, 999)}";
        var actor = new EvidenceActor { Id = fixture.People.Add("Val Validator") };
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), actor, new Dictionary<string, string?> { ["Release:Version"] = version });
        await using var scope = provider.CreateAsyncScope();
        var packs = scope.ServiceProvider.GetRequiredService<ValidationPacks>();
        var before = await packs.InstalledAsync(Cancel);
        var missing = await packs.OpenAsync(Cancel);

        var content = "a validation pack, as the release built it"u8.ToArray();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        await using (var stream = new MemoryStream(content))
        {
            await storage.PutAsync(ValidationPacks.KeyOf(version), stream, "application/zip", Cancel);
        }

        await using (var other = new MemoryStream("another version's pack"u8.ToArray()))
        {
            await storage.PutAsync(ValidationPacks.KeyOf("2031.01.01.1"), other, "application/zip", Cancel);
        }

        var after = await packs.InstalledAsync(Cancel);
        var opened = await packs.OpenAsync(Cancel);
        var context = (ContextRelease)await scope.ServiceProvider.GetServices<IContextPart>().Single(p => p.Name == ReleaseContextPart.PartName).BuildAsync(actor.Id, Cancel);
        var audited = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.EventType == ComplianceAuditEvents.ValidationPackDownloaded && e.ActorId == actor.Id)
            .SingleAsync(Cancel);

        Assert.Equal((version, false, false), (before.Version, before.Development, before.Available));
        Assert.Equal(ComplianceErrors.ValidationPackMissing, missing.Error.Code);
        Assert.True(after.Available);
        Assert.Equal(content, opened.Value.Content);
        Assert.Equal($"validation-pack-{version}.zip", opened.Value.FileName);
        Assert.Equal((version, false), (context.Version, context.Development));
        Assert.Equal(version, JsonDocument.Parse(audited.Details!).RootElement.GetProperty("version").GetString());
    }

    [Fact]
    [Proves("TF-218 AC3", "ADR-0052")]
    public async Task ABuildWithoutAReleaseVersionIsADevelopmentBuildAndServesTheLocalPack()
    {
        var actor = new EvidenceActor { Id = fixture.People.Add("Dev Developer") };
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), actor);
        await using var scope = provider.CreateAsyncScope();
        await using (var stream = new MemoryStream("the latest local pack"u8.ToArray()))
        {
            await scope.ServiceProvider.GetRequiredService<IObjectStorage>().PutAsync(ValidationPacks.KeyOf(ReleaseOptions.Development), stream, "application/zip", Cancel);
        }

        var installed = await scope.ServiceProvider.GetRequiredService<ValidationPacks>().InstalledAsync(Cancel);
        var opened = await scope.ServiceProvider.GetRequiredService<ValidationPacks>().OpenAsync(Cancel);

        Assert.Equal((ReleaseOptions.Development, true, true), (installed.Version, installed.Development, installed.Available));
        Assert.Equal("validation-pack-development.zip", opened.Value.FileName);
    }
}
