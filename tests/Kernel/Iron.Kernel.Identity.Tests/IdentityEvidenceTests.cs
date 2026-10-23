using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Identity.Tests;

/// <summary>Identity's part of an evidence pack (S3 T8, TF-067, ADR-0052): people and accounts as they stood at the period's end.</summary>
[Collection(IdentityCollection.Name)]
public class IdentityEvidenceTests(IdentityFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-067 AC1", "TF-062 AC4", "ADR-0052")]
    public async Task PeopleAndEmergencyAccountsAreListedAsTheyStoodAtThePeriodsEndNotAsTheyAreNow()
    {
        var database = await fixture.CreateTenantDatabaseAsync($"evidence_{Ids.New():N}");
        var start = new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start.AddHours(1));
        await using var provider = fixture.BuildServices(time, connectionString: database);
        var keeper = $"keeper-{Ids.New():N}@acme.test";
        var newcomer = $"newcomer-{Ids.New():N}@acme.test";
        Principal person;
        await using (var scope = provider.CreateAsyncScope())
        {
            person = await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, keeper, "correct horse battery staple", "Kim Keeper");
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            context.Principals.Attach(person);
            person.MarkEmergency(true);
            await context.SaveChangesAsync(Cancel);
        }

        var end = start.AddDays(1);
        time.SetUtcNow(start.AddDays(2));
        await using (var scope = provider.CreateAsyncScope())
        {
            await IdentityFixture.CreateActiveUserAsync(scope.ServiceProvider, newcomer, "correct horse battery staple", "Nia Newcomer");
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var now = await context.Principals.SingleAsync(p => p.Id == person.Id, Cancel);
            now.Deactivate();
            now.MarkEmergency(false);
            await context.SaveChangesAsync(Cancel);
        }

        IReadOnlyList<EvidenceTable> tables;
        await using (var scope = provider.CreateAsyncScope())
        {
            var source = scope.ServiceProvider.GetServices<IEvidenceSource>().OfType<IdentityEvidence>().Single();
            tables = await source.TablesAsync(new EvidencePeriod(start, end), Cancel);
        }

        var people = tables.Single(t => t.Name == "people_and_accounts");
        var row = Assert.Single(people.Rows, r => r[1] == keeper);
        Assert.Equal(("Kim Keeper", "person", "active", "yes"), (row[2], row[3], row[4], row[7]));
        Assert.DoesNotContain(people.Rows, r => r[1] == newcomer);
        Assert.Contains(tables.Single(t => t.Name == "emergency_accounts").Rows, r => r[0] == keeper);
        Assert.Equal(EvidenceDocuments.AccessList, people.Document);
        Assert.All(tables.Where(t => t.Name != "people_and_accounts"), t => Assert.Equal(EvidenceDocuments.SecuritySettings, t.Document));
    }
}
