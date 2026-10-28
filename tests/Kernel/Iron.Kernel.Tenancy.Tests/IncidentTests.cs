using Iron.Kernel.Audit.Data;
using Iron.Kernel.Tenancy.Api;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>Incidents as the runbook runs them in the control plane (TF-079 AC2, ADR-0056).</summary>
[Collection(TenancyCollection.Name)]
public class IncidentTests(TenancyFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-079 AC2", "ADR-0056")]
    public async Task AnIncidentDeclaredRunByTheRunbookAndClosedHasItsReviewRecordedWithinTenDays()
    {
        var operatorId = Guid.NewGuid();
        var time = new FakeTimeProvider(Now());
        var tenant = await TenantAsync(time);
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(operatorId));
        await using var scope = provider.CreateAsyncScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();

        var declared = await incidents.DeclareAsync(new IncidentDeclaration(2, "Sign-ins slow for one tenant", "Olga Operator", "Carl Comms", "Sam Scribe", [new AffectedTenant(tenant, false, null)]), Cancel);
        var id = declared.Value.Incident.Id;
        time.Advance(TimeSpan.FromMinutes(20));
        await incidents.NoteAsync(id, "note", "The database's connections were saturated; the pool was raised.", null, Cancel);
        await incidents.NoteAsync(id, "communication", "Told the tenant's administrators in their channel.", tenant, Cancel);
        time.Advance(TimeSpan.FromHours(2));
        var closed = await incidents.CloseAsync(id, "Sign-ins are back to normal speed.", Cancel);
        time.Advance(TimeSpan.FromDays(3));
        var reviewed = await incidents.ReviewAsync(id, "The pool was too small for the morning's peak.", "Sizing was never revisited after the tenant grew.", "The pool follows the size class; an alert on saturation.", Cancel);

        Assert.Equal(closed.Value.Incident.ClosedAt!.Value.AddDays(10), closed.Value.Incident.ReviewDueAt);
        var response = IncidentViews.Response(reviewed.Value, time.GetUtcNow());
        Assert.Equal("recorded", response.ReviewState);
        Assert.True(response.Review!.OnTime);
        Assert.Equal(["declared", "note", "communication", "closed", "review"], response.Timeline.Select(e => e.Kind));
        Assert.All(response.Timeline, e => Assert.Equal(operatorId.ToString("D"), e.By));
        Assert.Equal(("Olga Operator", "Carl Comms", "Sam Scribe"), (response.Lead, response.Communications, response.Scribe));

        // Every step is in the control plane's trail, with the operator.
        var events = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.RecordId == id).OrderBy(e => e.CreatedAt).ToListAsync(Cancel);
        Assert.Equal([TenancyAuditEvents.IncidentDeclared, TenancyAuditEvents.IncidentNoted, TenancyAuditEvents.IncidentNoted, TenancyAuditEvents.IncidentClosed, TenancyAuditEvents.IncidentReviewRecorded], events.Select(e => e.EventType));
        Assert.All(events, e => Assert.Equal(operatorId, e.ActorId));
    }

    [Fact]
    [Proves("TF-079 AC2", "ADR-0056")]
    public async Task AReviewNotRecordedTenDaysAfterTheCloseIsEscalatedOnceOnDayTen()
    {
        var time = new FakeTimeProvider(Now());
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(Guid.NewGuid()));
        await using var scope = provider.CreateAsyncScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
        var escalation = scope.ServiceProvider.GetRequiredService<IncidentEscalation>();
        var id = (await incidents.DeclareAsync(new IncidentDeclaration(3, "A certificate expired on a status page", "Olga Operator", null, null, []), Cancel)).Value.Incident.Id;
        await incidents.CloseAsync(id, "Renewed.", Cancel);

        time.Advance(TimeSpan.FromDays(10) - TimeSpan.FromMinutes(1));
        await escalation.RunAsync(Cancel);
        Assert.Equal(0, await EscalationsAsync(incidents, id));
        time.Advance(TimeSpan.FromMinutes(1));
        await escalation.RunAsync(Cancel);
        Assert.Equal(1, await EscalationsAsync(incidents, id));
        time.Advance(TimeSpan.FromHours(1));
        await escalation.RunAsync(Cancel);
        Assert.Equal(1, await EscalationsAsync(incidents, id));

        var record = (await incidents.RecordAsync(id, Cancel)).Value;
        Assert.Equal("overdue", IncidentViews.Response(record, time.GetUtcNow()).ReviewState);
        Assert.Equal(IncidentEntry.Escalated, record.Entries[^1].Kind);
        var overdue = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().SingleAsync(e => e.RecordId == id && e.EventType == TenancyAuditEvents.IncidentReviewOverdue, Cancel);
        Assert.Equal("high", overdue.Severity);

        // Recorded late, it says so.
        var late = await incidents.ReviewAsync(id, "It lapsed.", "Nobody owned its renewal.", "Renewal is automated and watched.", Cancel);
        Assert.False(IncidentViews.Response(late.Value, time.GetUtcNow()).Review!.OnTime);
    }

    [Fact]
    [Proves("TF-079 AC2", "ADR-0056")]
    public async Task APersonalDataBreachShowsTheSeventyTwoHourDeadlinePerTenantAndCannotCloseUntilEachIsTold()
    {
        var time = new FakeTimeProvider(Now());
        var first = await TenantAsync(time);
        var second = await TenantAsync(time);
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(Guid.NewGuid()));
        await using var scope = provider.CreateAsyncScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
        var escalation = scope.ServiceProvider.GetRequiredService<IncidentEscalation>();
        var knownAt = time.GetUtcNow().AddHours(-6);

        var declared = await incidents.DeclareAsync(new IncidentDeclaration(1, "An export link reached the wrong person", "Olga Operator", "Carl Comms", null, [new AffectedTenant(first, true, knownAt)]), Cancel);
        var id = declared.Value.Incident.Id;
        time.Advance(TimeSpan.FromHours(10));
        await incidents.AffectAsync(id, new AffectedTenant(second, true, null), Cancel);

        var record = IncidentViews.Response((await incidents.RecordAsync(id, Cancel)).Value, time.GetUtcNow());
        var one = record.Tenants.Single(t => t.Tenant == first);
        var two = record.Tenants.Single(t => t.Tenant == second);
        Assert.Equal((knownAt.AddHours(72), "due"), (one.NoticeDueAt!.Value, one.NoticeState));
        Assert.Equal((time.GetUtcNow().AddHours(72), "due"), (two.NoticeDueAt!.Value, two.NoticeState));
        Assert.Contains(record.Timeline, e => e.Kind == "tenant" && e.Tenant == second && e.Text.Contains("tell it by", StringComparison.Ordinal));

        // The first is told in time; the second is not, and is escalated when its 72 hours are up.
        await incidents.NoteAsync(id, "notice", "We told your data protection officer by email: one export link reached a person outside your organisation.", first, Cancel);
        var refused = await incidents.CloseAsync(id, "Contained.", Cancel);
        Assert.Equal(TenancyErrors.IncidentNoticeMissing, refused.Error.Code);
        time.Advance(TimeSpan.FromHours(72));
        await escalation.RunAsync(Cancel);
        Assert.Equal(1, await EscalationsAsync(incidents, id));

        record = IncidentViews.Response((await incidents.RecordAsync(id, Cancel)).Value, time.GetUtcNow());
        Assert.Equal("notified", record.Tenants.Single(t => t.Tenant == first).NoticeState);
        Assert.Equal("overdue", record.Tenants.Single(t => t.Tenant == second).NoticeState);
        await incidents.NoteAsync(id, "notice", "We told your data protection officer by email, late.", second, Cancel);
        Assert.True((await incidents.CloseAsync(id, "Contained.", Cancel)).IsSuccess);
        Assert.Equal("notified_late", IncidentViews.Response((await incidents.RecordAsync(id, Cancel)).Value, time.GetUtcNow()).Tenants.Single(t => t.Tenant == second).NoticeState);
    }

    [Fact]
    [Proves("ADR-0056")]
    public async Task AnIncidentRefusesWhatTheRunbookDoesNotAllow()
    {
        var time = new FakeTimeProvider(Now());
        var tenant = await TenantAsync(time);
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(Guid.NewGuid()));
        await using var scope = provider.CreateAsyncScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();

        var severity = await incidents.DeclareAsync(new IncidentDeclaration(5, "x", "y", null, null, []), Cancel);
        var unknown = await incidents.DeclareAsync(new IncidentDeclaration(3, "x", "y", null, null, [new AffectedTenant("nobody-here", false, null)]), Cancel);
        var future = await incidents.DeclareAsync(new IncidentDeclaration(3, "x", "y", null, null, [new AffectedTenant(tenant, true, time.GetUtcNow().AddHours(1))]), Cancel);
        var id = (await incidents.DeclareAsync(new IncidentDeclaration(4, "A test", "Olga Operator", null, null, [new AffectedTenant(tenant, false, null)]), Cancel)).Value.Incident.Id;
        var notDue = await incidents.NoteAsync(id, "notice", "Nothing to tell.", tenant, Cancel);
        var early = await incidents.ReviewAsync(id, "a", "b", "c", Cancel);

        Assert.Equal(TenancyErrors.IncidentInvalid, severity.Error.Code);
        Assert.Equal(TenancyErrors.TenantNotFound, unknown.Error.Code);
        Assert.Equal(TenancyErrors.IncidentInvalid, future.Error.Code);
        Assert.Equal(TenancyErrors.IncidentInvalid, notDue.Error.Code);
        Assert.Equal(TenancyErrors.IncidentOpen, early.Error.Code);
    }

    /// <summary>The escalations in this incident's timeline: the pass runs over every incident of the shared database.</summary>
    private static async Task<int> EscalationsAsync(IncidentService incidents, Guid id) =>
        (await incidents.RecordAsync(id, Cancel)).Value.Entries.Count(e => e.Kind == IncidentEntry.Escalated);

    /// <summary>Now in whole seconds, so a time reads back from the database as it was written.</summary>
    private static DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
    }

    private async Task<string> TenantAsync(FakeTimeProvider time)
    {
        var slug = $"inc{Guid.NewGuid():N}"[..20];
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(SystemPrincipal.SystemId));
        await using var scope = provider.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<TenantRegistry>().CreateAsync(new NewTenant(slug, slug, [], $"admin@{slug}.test", null, SizeClass.Small, null, null), Cancel)).IsSuccess);
        return slug;
    }
}
