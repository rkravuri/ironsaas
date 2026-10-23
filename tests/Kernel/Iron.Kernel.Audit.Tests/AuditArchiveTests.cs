using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.Files.Contracts;
using Iron.Kernel.Fleet.Services;
using Iron.Kernel.Tenancy.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Audit.Tests;

/// <summary>
/// The archive and the trail's retention (TF-064, TF-066, ADR-0050): events older than the online period leave the
/// database for the tenant's container with their chain, stay searchable by date range through the archive index, and
/// still verify; the oldest end goes after the retention, anchored, and the rest still verifies; a hold on the whole
/// tenant keeps it; a retention below the operator's minimum is refused.
/// </summary>
[Collection(AuditCollection.Name)]
public class AuditArchiveTests(AuditFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2025, 1, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    [Proves("TF-064 AC2", "ADR-0050")]
    public async Task ArchivedEventsLeaveTheDatabaseKeepTheirChainAndStaySearchableByDateRange()
    {
        var (time, database, container, provider) = await ArchivedChainAsync("archived");
        await using (provider)
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
                Assert.False(await context.Events.AnyAsync(e => e.ChainPosition <= 6, Cancel));
                var batch = Assert.Single(await context.ArchiveBatches.AsNoTracking().ToListAsync(Cancel));
                Assert.Equal((1L, 6L, 6), (batch.FirstPosition, batch.LastPosition, batch.EventCount));

                var (found, batches, truncated) = await scope.ServiceProvider.GetRequiredService<AuditArchiveSearch>().SearchAsync(
                    new ArchiveQuery(Start.AddDays(-1), Start.AddDays(1), "tests.chain.event", null, null, null, null, null), Cancel);
                Assert.Equal((1, false), (batches, truncated));
                Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], found.Select(e => e.Position));
            }

            // The verification reads the archive with the database: one chain, still whole.
            var anchors = await AuditChainTests.AnchorsOfAsync(provider);
            var verification = await Verifier(anchors, database).VerifyAsync(AuditChainTests.Due(database, full: true, container), Cancel);
            Assert.Equal(AuditVerificationResults.Passed, verification.Result);
            Assert.Equal(1L, verification.FromPosition);
            Assert.True(verification.ToPosition >= 9);
            _ = time;
        }
    }

    [Fact]
    [Proves("ADR-0050")]
    public async Task PruningRemovesTheOldestEndAndTheRestStillVerifiesFromItsAnchor()
    {
        var (time, database, container, provider) = await ArchivedChainAsync("pruned");
        await using (provider)
        {
            time.Advance(TimeSpan.FromDays(8 * 365));
            AuditPrune? prune;
            await using (var scope = provider.CreateAsyncScope())
            {
                prune = await scope.ServiceProvider.GetRequiredService<AuditPruner>().PruneAsync(Cancel);
                Assert.NotNull(prune);
                Assert.Equal(6L, prune.UpToPosition);
                Assert.Null(await scope.ServiceProvider.GetRequiredService<IObjectStorage>().GetAsync(AuditArchiveFormat.KeyOf(1, 6), Cancel));
            }

            await AuditChainTests.SealEverythingAsync(provider);
            var anchors = await AuditChainTests.AnchorsOfAsync(provider);
            anchors.Add(new AuditAnchorInfo(AuditAnchorKinds.Pruned, prune.UpToPosition, prune.HashAtPosition, prune.CreatedAt, prune.CreatedAt));
            var verification = await Verifier(anchors, database).VerifyAsync(AuditChainTests.Due(database, full: true, container), Cancel);
            Assert.Equal(AuditVerificationResults.Passed, verification.Result);
            Assert.Equal(7L, verification.FromPosition);
        }
    }

    [Fact]
    [Proves("TF-066 AC2", "ADR-0050")]
    public async Task AHoldOnTheWholeTenantKeepsTheTrailsOldestEnd()
    {
        var (time, _, _, provider) = await ArchivedChainAsync("held");
        await using (provider)
        {
            time.Advance(TimeSpan.FromDays(8 * 365));
            Guid holdId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var hold = await scope.ServiceProvider.GetRequiredService<LegalHolds>().PlaceAsync(LegalHoldScopes.Tenant, null, null, "Regulator's inquiry", Cancel);
                holdId = hold.Value.Id;
                Assert.Null(await scope.ServiceProvider.GetRequiredService<AuditPruner>().PruneAsync(Cancel));
            }

            await using (var scope = provider.CreateAsyncScope())
            {
                Assert.True((await scope.ServiceProvider.GetRequiredService<LegalHolds>().ReleaseAsync(holdId, "Inquiry closed", Cancel)).IsSuccess);
                Assert.NotNull(await scope.ServiceProvider.GetRequiredService<AuditPruner>().PruneAsync(Cancel));
            }
        }
    }

    [Fact]
    [Proves("TF-066 AC3", "ADR-0050")]
    public async Task ARetentionBelowTheOperatorsMinimumIsRefusedAndOneWithinItIsKept()
    {
        var settings = new Dictionary<string, string?> { ["Retention:Limits:session:MinimumDays"] = "180" };
        await using var provider = fixture.BuildServices(new FakeTimeProvider(Start), new FixedPrincipal(Ids.New()), settings: settings);
        await using var scope = provider.CreateAsyncScope();
        var policy = scope.ServiceProvider.GetRequiredService<RetentionPolicy>();

        var refused = await policy.ChooseAsync(RetentionClasses.Session.Code, 90, "shorter", Cancel);
        Assert.True(refused.IsFailure);
        Assert.Equal(AuditErrors.RetentionBelowMinimum, refused.Error.Code);
        Assert.Equal(RetentionClasses.Session.DefaultDays, await policy.DaysAsync(RetentionClasses.Session, Cancel));

        var kept = await policy.ChooseAsync(RetentionClasses.Session.Code, 200, "a policy review", Cancel);
        Assert.True(kept.IsSuccess);
        Assert.Equal(200, await policy.DaysAsync(RetentionClasses.Session, Cancel));
        Assert.Equal((180, 200), ((await policy.ClassesAsync(Cancel)).Single(c => c.Class == RetentionClasses.Session).MinimumDays, kept.Value.Days));
    }

    /// <summary>
    /// A chain of its own: six events in the first week of 2025 and three fourteen months later, all sealed, then the
    /// archive pass: the six go to the scenario's own container.
    /// </summary>
    private async Task<(FakeTimeProvider Time, string Database, string Container, ServiceProvider Provider)> ArchivedChainAsync(string scenario)
    {
        var time = new FakeTimeProvider(Start);
        var database = await fixture.NewChainDatabaseAsync(scenario);
        var container = $"audit-{scenario}-{Guid.NewGuid():N}"[..30];
        var provider = fixture.BuildServices(time, new FixedPrincipal(SystemPrincipal.SystemId), database, container);
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IObjectStorage>().EnsureContainerAsync(Cancel);
        }

        await AuditChainTests.WriteAsync(provider, 6);
        await AuditChainTests.SealEverythingAsync(provider);
        time.Advance(TimeSpan.FromDays(14 * 30));
        await AuditChainTests.WriteAsync(provider, 3);
        await AuditChainTests.SealEverythingAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.Equal(6, await scope.ServiceProvider.GetRequiredService<AuditArchiver>().ArchiveAsync(Cancel));
        }

        await AuditChainTests.SealEverythingAsync(provider);
        return (time, database, container, provider);
    }

    private AuditChainVerifier Verifier(List<AuditAnchorInfo> anchors, string database)
    {
        var options = new FleetOptions();
        options.Database.AdminConnectionString = database;
        options.Storage.AccountConnectionString = fixture.StorageConnectionString;
        var secrets = new MemorySecrets();
        secrets.Values[$"tenants/{AuditFixture.ChainTenant}/database"] = new Dictionary<string, string> { ["audit_password"] = AuditFixture.ReaderPassword };
        return new AuditChainVerifier(new FakeChains(anchors), secrets, options, new ArchivedChainReader(options), TimeProvider.System);
    }
}
