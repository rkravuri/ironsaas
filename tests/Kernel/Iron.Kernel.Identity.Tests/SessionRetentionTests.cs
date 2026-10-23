using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// The kernel's own sessions under their retention class (TF-066, ADR-0050): a session past its twelve months is archived
/// and deleted with an audit event, by the retention job of the Audit module; a person under a legal hold keeps theirs.
/// </summary>
[Collection(IdentityCollection.Name)]
public class SessionRetentionTests(IdentityFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-066 AC1", "TF-066 AC2", "ADR-0050")]
    public async Task ASessionPastItsRetentionIsArchivedAndDeletedWithAnEventUnlessItsPersonIsHeld()
    {
        var archive = new RecordingArchive();
        // A database of its own: the year the test moves the clock on would end every other test's sessions too.
        var database = await fixture.CreateTenantDatabaseAsync($"retention_{Ids.New():N}"[..24]);
        await using var host = await UnitHost.StartAsync(fixture, connectionString: database, services: services => services.AddSingleton<IRecordArchive>(archive));
        var held = await host.PersonAsync();
        var free = await host.PersonAsync();
        Session heldSession;
        Session freeSession;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            heldSession = (await sessions.StartAsync(held, Cancel)).Session;
            freeSession = (await sessions.StartAsync(free, Cancel)).Session;
            var hold = await scope.ServiceProvider.GetRequiredService<LegalHolds>().PlaceAsync(LegalHoldScopes.Master, UserService.RecordType, held.Id, "Employment tribunal", Cancel);
            Assert.True(hold.IsSuccess);
        }

        // A year and a little more after both sessions' limits passed.
        host.Time.Advance(TimeSpan.FromDays(RetentionClasses.Session.DefaultDays + 2));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var runs = await scope.ServiceProvider.GetRequiredService<RecordRetentionRunner>().RunAsync(Cancel);
            Assert.Contains(runs, r => r.RecordType == SessionRetention.Type && r.Deleted >= 1 && r.Held >= 1);
        }

        Assert.Contains(archive.Archived, a => a.Id == freeSession.Id);
        Assert.DoesNotContain(archive.Archived, a => a.Id == heldSession.Id);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            Assert.False(await context.Sessions.AnyAsync(s => s.Id == freeSession.Id, Cancel));
            Assert.True(await context.Sessions.AnyAsync(s => s.Id == heldSession.Id, Cancel));
            var expired = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
                .SingleAsync(e => e.EventType == AuditEvents.RecordExpired && e.RecordId == freeSession.Id, Cancel);
            Assert.Equal(SessionRetention.Type, expired.RecordType);
            Assert.Equal($"archive/{freeSession.Id:D}", System.Text.Json.JsonDocument.Parse(expired.Details!).RootElement.GetProperty("archive_key").GetString());
        }
    }

    private sealed class RecordingArchive : IRecordArchive
    {
        public List<RetainedRecord> Archived { get; } = [];

        public Task<string> ArchiveAsync(string recordType, RetentionClass retention, RetainedRecord record, CancellationToken cancellationToken)
        {
            Archived.Add(record);
            return Task.FromResult($"archive/{record.Id:D}");
        }
    }
}
