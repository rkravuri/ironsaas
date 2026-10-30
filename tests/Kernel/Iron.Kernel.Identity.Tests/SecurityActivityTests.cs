using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// Another module's change to a person's access in the person's security activity (ADR-0046, ADR-0057): the row, with
/// the administrator who made it named, or the person themselves; the module audits the change, so Identity writes no
/// audit event of its own for it.
/// </summary>
[Collection(IdentityCollection.Name)]
public class SecurityActivityTests(IdentityFixture fixture)
{
    [Fact]
    [Proves("ADR-0057", "ADR-0046")]
    public async Task AChangeOfAPersonsVisibilityTagsIsNotedInTheirSecurityActivityNamingWhoMadeIt()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var administrator = await host.PersonAsync(displayName: "Tag Administrator");
        var person = await host.PersonAsync();

        await host.WithScopeAsync(async services =>
        {
            var context = services.GetRequiredService<IdentityDbContext>();
            await new SecurityActivity(context, new Actor(administrator.Id), TimeProvider.System).NoteAsync(person.Id, SecurityEventKinds.VisibilityTagsChanged, TestContext.Current.CancellationToken);
            await new SecurityActivity(context, new Actor(person.Id), TimeProvider.System).NoteAsync(person.Id, SecurityEventKinds.VisibilityTagsChanged, TestContext.Current.CancellationToken);
            return 0;
        });

        var noted = (await host.SecurityEventsAsync(person.Id)).Where(e => e.Kind == SecurityEventKinds.VisibilityTagsChanged).ToList();
        Assert.Contains(noted, e => e.Actor == SecurityEventActors.Administrator && e.ActorName == "Tag Administrator");
        Assert.Contains(noted, e => e.Actor == SecurityEventActors.Self && e.ActorName is null);
        Assert.Empty(await host.AuditAsync(SecurityEventKinds.AuditEventOf(SecurityEventKinds.VisibilityTagsChanged)));
    }

    private sealed class Actor(Guid id) : ICurrentPrincipal
    {
        public Guid Id => id;
    }
}
