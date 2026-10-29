using System.Text.Json;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Visibility;
using Iron.Kernel.History;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Visibility.Contracts;
using Iron.Kernel.Visibility.Data;
using Iron.Kernel.Visibility.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Visibility.Tests;

/// <summary>
/// The Visibility module against PostgreSQL (TF-241, ADR-0057): a tag's life, each change audited and tracked; who holds
/// which tag under the escalation rule; the audit of a holder's change with before and after and its note in their
/// security activity; a service account's tags; and the tags of the delegator for a person acting for them.
/// </summary>
[Collection(VisibilityCollection.Name)]
public class VisibilityTagTests(VisibilityFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly TestPrincipals _people = new();

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("ADR-0057", "ADR-0051")]
    public async Task ATagIsCreatedInCapitalsRenamedRetiredAndReactivatedEachChangeAuditedAndTrackedAndItsCodeNeverReused()
    {
        var admin = _people.Add("Tag administrator");
        var code = VisibilityFixture.Code("Apac");
        await using var provider = fixture.BuildServices(_people, new FixedPrincipal(admin));
        await using var scope = provider.CreateAsyncScope();
        var tags = scope.ServiceProvider.GetRequiredService<TagService>();

        var created = await tags.CreateAsync(code.ToLowerInvariant(), " Asia Pacific ", "Our region", Cancel);
        var asCreated = (created.Value.Code, created.Value.Name, created.Value.Description);
        var again = await tags.CreateAsync(code, "Twice", null, Cancel);
        var invalid = await tags.CreateAsync("has space", "Invalid", null, Cancel);
        var renamed = await tags.ChangeAsync(created.Value.Id, "Asia and the Pacific", null, Cancel);
        var asRenamed = (renamed.Value.Name, renamed.Value.Description);
        var retiredActive = (await tags.RetireAsync(created.Value.Id, Cancel)).Value.IsActive;
        var retiredAgain = await tags.RetireAsync(created.Value.Id, Cancel);
        var reactivated = await tags.ReactivateAsync(created.Value.Id, Cancel);

        Assert.Equal((code, "Asia Pacific", (string?)"Our region"), asCreated);
        Assert.Equal(VisibilityErrors.TagCodeTaken, again.Error.Code);
        Assert.Equal(VisibilityErrors.TagCodeInvalid, invalid.Error.Code);
        Assert.Equal(("Asia and the Pacific", (string?)null), asRenamed);
        Assert.False(retiredActive);
        Assert.Equal(VisibilityErrors.TagRetired, retiredAgain.Error.Code);
        Assert.True(reactivated.Value.IsActive);

        var events = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.RecordId == created.Value.Id).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).Select(e => e.EventType).ToListAsync(Cancel);
        Assert.Equal(
            [VisibilityAuditEvents.TagCreated, VisibilityAuditEvents.TagChanged, VisibilityAuditEvents.TagRetired, VisibilityAuditEvents.TagReactivated],
            events.Where(e => e.StartsWith("visibility.", StringComparison.Ordinal)));
        Assert.Equal(["record.created", "record.changed", "record.changed", "record.changed"], events.Where(e => e.StartsWith("record.", StringComparison.Ordinal)));
    }

    [Fact]
    [Proves("TF-241 AC7", "ADR-0057")]
    public async Task GivingAPersonTagsIsAuditedOnTheirRecordWithBeforeAndAfterAndNotedInTheirSecurityActivity()
    {
        var admin = _people.Add("Holder administrator");
        var person = _people.Add("Tagged person");
        var (first, second) = (await TagAsync(admin, "One"), await TagAsync(admin, "Two"));
        var activity = new NotedActivity();

        await using var provider = fixture.BuildServices(_people, new FixedPrincipal(admin), activity);
        await using var scope = provider.CreateAsyncScope();
        var holdings = scope.ServiceProvider.GetRequiredService<HoldingService>();
        Assert.True((await holdings.SetAsync(person, "Tagged person", [second, first.ToLowerInvariant()], Cancel)).IsSuccess);
        Assert.True((await holdings.SetAsync(person, "Tagged person", [second], Cancel)).IsSuccess);

        var changes = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.RecordId == person && e.EventType == VisibilityAuditEvents.HolderTagsChanged)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .Select(e => new { e.RecordType, e.Changes, e.ActorId })
            .ToListAsync(Cancel);
        string[] both = [first, second];
        Assert.Equal(
            [("[]", JsonSerializer.Serialize(both.Order(StringComparer.Ordinal))), (JsonSerializer.Serialize(both.Order(StringComparer.Ordinal)), JsonSerializer.Serialize(new[] { second }))],
            changes.Select(c => Assert.Single(JsonSerializer.Deserialize<List<FieldChange>>(c.Changes!, Json)!)).Select(f => (f.Old, f.New)));
        Assert.All(changes, c => Assert.Equal((IdentityRecordTypes.Principal, admin), (c.RecordType!, c.ActorId)));
        Assert.Equal([(person, SecurityEventKinds.VisibilityTagsChanged), (person, SecurityEventKinds.VisibilityTagsChanged)], activity.Notes);
    }

    [Fact]
    [Proves("ADR-0057")]
    public async Task APersonGivesAndTakesAwayOnlyTheTagsTheyHoldUnlessTheyHoldNone()
    {
        var anyone = _people.Add("Holds no tag");
        var limited = _people.Add("Holds one tag");
        var person = _people.Add("Given tags");
        var (mine, theirs) = (await TagAsync(anyone, "Mine"), await TagAsync(anyone, "Theirs"));
        await SetAsync(anyone, limited, mine);
        await SetAsync(anyone, person, theirs);

        await using var provider = fixture.BuildServices(_people, new FixedPrincipal(limited));
        await using var scope = provider.CreateAsyncScope();
        var holdings = scope.ServiceProvider.GetRequiredService<HoldingService>();
        var given = await holdings.SetAsync(person, "Given tags", [theirs, mine], Cancel);
        var foreign = await holdings.SetAsync(person, "Given tags", [theirs, mine, VisibilityFixture.Code("Other")], Cancel);
        var takenAway = await holdings.SetAsync(person, "Given tags", [mine], Cancel);
        var givable = await holdings.GivableAsync(Cancel);

        Assert.Equal(new[] { mine, theirs }.Order(StringComparer.Ordinal), given.Value.Select(t => t.Code));
        Assert.Equal(VisibilityErrors.TagNotFound, foreign.Error.Code);
        Assert.Equal(VisibilityErrors.TagNotHeld, takenAway.Error.Code);
        Assert.Contains(theirs, takenAway.Error.Message, StringComparison.Ordinal);
        Assert.Equal([mine], givable.Select(t => t.Code));

        var refused = await holdings.SetAsync(person, "Given tags", [theirs, mine, await TagAsync(anyone, "Third")], Cancel);
        Assert.Equal(VisibilityErrors.TagNotHeld, refused.Error.Code);
    }

    [Fact]
    [Proves("ADR-0057")]
    public async Task ARetiredTagIsGivenToNobodyAndStaysWithItsHoldersWhoAreStillFilteredByIt()
    {
        var admin = _people.Add("Retiring administrator");
        var holder = _people.Add("Keeps the tag");
        var other = _people.Add("Wants the tag");
        var code = await TagAsync(admin, "Old");
        await SetAsync(admin, holder, code);

        await using var provider = fixture.BuildServices(_people, new FixedPrincipal(admin));
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VisibilityDbContext>();
        var id = await context.Tags.Where(t => t.Code == code).Select(t => t.Id).SingleAsync(Cancel);
        Assert.True((await scope.ServiceProvider.GetRequiredService<TagService>().RetireAsync(id, Cancel)).IsSuccess);
        var refused = await scope.ServiceProvider.GetRequiredService<HoldingService>().SetAsync(other, "Wants the tag", [code], Cancel);

        Assert.Equal(VisibilityErrors.TagRetired, refused.Error.Code);
        Assert.Equal([code], await scope.ServiceProvider.GetRequiredService<IVisibilityTags>().HeldAsync(holder, Cancel));
        Assert.DoesNotContain(await scope.ServiceProvider.GetRequiredService<IVisibilityTags>().ActiveAsync(Cancel), t => t.Code == code);
    }

    [Fact]
    [Proves("ADR-0057")]
    public async Task AServiceAccountHoldsTagsAndTheRecordFilterReadsThem()
    {
        var admin = _people.Add("Service administrator");
        var client = _people.Add("Reporting client", PrincipalKind.Service);
        var code = await TagAsync(admin, "Feed");
        await SetAsync(admin, client, code);

        await using var provider = fixture.BuildServices(_people, new FixedPrincipal(admin));
        await using var scope = provider.CreateAsyncScope();

        Assert.Equal([code], await scope.ServiceProvider.GetRequiredService<IVisibilityTags>().HeldAsync(client, Cancel));
        Assert.Contains(await scope.ServiceProvider.GetRequiredService<IVisibilityTagHolders>().HoldingsAsync([client], Cancel), h => h.Code == code && h.PrincipalId == client);
    }

    [Fact]
    [Proves("ADR-0037", "ADR-0057")]
    public async Task WhileActingForSomeoneAPersonIsFilteredByTheDelegatorsTags()
    {
        var admin = _people.Add("Acting administrator");
        var delegator = _people.Add("Delegator");
        var delegated = _people.Add("Delegate");
        var (theirs, own) = (await TagAsync(admin, "Delegator"), await TagAsync(admin, "Delegate"));
        await SetAsync(admin, delegator, theirs);
        await SetAsync(admin, delegated, own);

        await using var acting = fixture.BuildServices(_people, new FixedPrincipal(delegated), acting: new FixedActing(delegated, delegator));
        await using var scope = acting.CreateAsyncScope();

        Assert.Equal([theirs], await scope.ServiceProvider.GetRequiredService<IVisibilityTags>().HeldAsync(delegated, Cancel));
    }

    private async Task<string> TagAsync(Guid admin, string prefix)
    {
        await using var provider = fixture.BuildServices(_people, new FixedPrincipal(admin));
        await using var scope = provider.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<TagService>().CreateAsync(VisibilityFixture.Code(prefix), prefix, null, Cancel);
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        return created.Value.Code;
    }

    /// <summary>Gives the tags as an administrator who holds none, so the escalation rule lets every tag through.</summary>
    private async Task SetAsync(Guid admin, Guid principal, params string[] codes)
    {
        await using var provider = fixture.BuildServices(_people, new FixedPrincipal(admin));
        await using var scope = provider.CreateAsyncScope();
        var set = await scope.ServiceProvider.GetRequiredService<HoldingService>().SetAsync(principal, "Someone", codes, Cancel);
        Assert.True(set.IsSuccess, set.IsFailure ? set.Error.Message : null);
    }
}
