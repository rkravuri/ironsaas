using System.Text.Json;
using Iron.Kernel.Api.ControlPlane;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Monitoring.Data;
using Iron.Kernel.Monitoring.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Monitoring.Tests;

/// <summary>What a rule runs with, what a tenant may change, and the way an alert travels to the operator (ADR-0056).</summary>
[Collection(MonitoringCollection.Name)]
public class RuleValuesTests(MonitoringFixture fixture)
{
    private static readonly AlertRule Denials = AlertRules.All.Single(r => r.Name == "permission_denials");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-079 AC4", "ADR-0056")]
    public void ARuleCountingATypeNoModuleDeclaresCannotRunAndSaysWhich()
    {
        var resolved = AlertRuleResolver.Resolve(Denials, new ConfigurationBuilder().Build(), null, new HashSet<string>(StringComparer.Ordinal) { "identity.login_failed" });

        Assert.True(resolved.IsFailure);
        Assert.Contains("It counts authz.permission_denied, which no module of this host declares.", resolved.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0056")]
    public void ATenantsValueAppliesOnlyWhereItIsStricterThanTheOperatorsEvenAfterTheOperatorTightens()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal) { "authz.permission_denied" };
        var tightened = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Monitoring:Rules:permission_denials:Threshold"] = "40", ["Monitoring:Rules:permission_denials:WindowMinutes"] = "30" }).Build();

        var stricter = AlertRuleResolver.Resolve(Denials, new ConfigurationBuilder().Build(), new TenantRuleValues(50, 20, null), declared).Value;
        var overtaken = AlertRuleResolver.Resolve(Denials, tightened, new TenantRuleValues(50, 20, null), declared).Value;

        Assert.Equal((50, 20), (stricter.Threshold, stricter.WindowMinutes));
        Assert.Equal((40, 30), (overtaken.Threshold, overtaken.WindowMinutes));
        Assert.Equal((40, 30), (overtaken.Operator.Threshold, overtaken.Operator.WindowMinutes));
    }

    [Fact]
    [Proves("TF-079 AC1", "ADR-0056")]
    public async Task ATenantCanMakeARuleStricterNeverLooserAndEachChangeIsAudited()
    {
        var tenant = await fixture.NewTenantAsync();
        var administrator = Guid.NewGuid();
        await using var provider = tenant.Build(new FakeTimeProvider(DateTimeOffset.UtcNow), administrator);
        await using var scope = provider.CreateAsyncScope();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();

        var higher = await alerts.SetAsync("permission_denials", new TenantRuleValues(150, null, null), Cancel);
        var shorter = await alerts.SetAsync("permission_denials", new TenantRuleValues(null, 5, null), Cancel);
        var atOnce = await alerts.SetAsync("impersonations", new TenantRuleValues(2, null, null), Cancel);
        var moreRows = await alerts.SetAsync("mass_exports", new TenantRuleValues(null, null, 20_000), Cancel);
        var stricter = await alerts.SetAsync("permission_denials", new TenantRuleValues(20, 30, null), Cancel);
        var fewerRows = await alerts.SetAsync("mass_exports", new TenantRuleValues(null, null, 1_000), Cancel);

        Assert.Equal(MonitoringErrors.RuleValueLooser, higher.Error.Code);
        Assert.Equal("looser", Assert.Single(higher.Error.Fields!).Code);
        Assert.Equal(MonitoringErrors.RuleValueLooser, shorter.Error.Code);
        Assert.Equal("fixed", Assert.Single(atOnce.Error.Fields!).Code);
        Assert.Equal(MonitoringErrors.RuleValueLooser, moreRows.Error.Code);
        Assert.Equal((20, 30), (stricter.Value.Effective!.Threshold, stricter.Value.Effective.WindowMinutes));
        Assert.Equal(1_000, fewerRows.Value.Effective!.Above);

        // Back to the operator's values: allowed, since they are the loosest a tenant may run with.
        var back = await alerts.SetAsync("permission_denials", new TenantRuleValues(null, null, null), Cancel);
        Assert.Equal((100, 10), (back.Value.Effective!.Threshold, back.Value.Effective.WindowMinutes));

        var changes = await scope.ServiceProvider.GetRequiredService<Iron.Kernel.Audit.Data.AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.EventType == MonitoringAuditEvents.RuleChanged).ToListAsync(Cancel);
        Assert.Equal(3, changes.Count);
        Assert.All(changes, e => Assert.Equal(administrator, e.ActorId));
    }

    [Fact]
    [Proves("ADR-0056")]
    public void AnAlertsLinksSearchItsEventTypesItsGroupAndItsTimesBothIncluded()
    {
        var first = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        var last = first.AddMinutes(8).AddTicks(1230);
        var person = "0198f0c2-0000-7000-8000-000000000001";
        var byAddress = AlertRules.All.Single(r => r.Name == "sign_in_failures_by_address");
        var exports = AlertRules.All.Single(r => r.Name == "mass_exports");

        Assert.Equal(
            $"/audit?event_type=authz.permission_denied&actor_id={person}&since=2026-10-29T10%3A00%3A00.000000Z&until=2026-10-29T10%3A08%3A00.000123Z",
            AlertLinks.Shell(Denials, person, first, last));
        Assert.Equal(
            $"/api/v1/audit-events?filter=event_type:eq:authz.permission_denied&filter=actor_id:eq:{person}&filter=created_at:gte:2026-10-29T10%3A00%3A00.000000Z&filter=created_at:lte:2026-10-29T10%3A08%3A00.000123Z&sort=-created_at&page_size=100",
            AlertLinks.Api(Denials, person, first, last));
        Assert.Contains("address=203.0.113.7", AlertLinks.Api(byAddress, "203.0.113.7", first, last), StringComparison.Ordinal);
        Assert.Contains("event_type:in:audit.events_exported%2Cauthz.roles_exported%2Cidentity.acceptances_exported", AlertLinks.Api(exports, person, first, last), StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0056")]
    public void ACopyNamesAPersonByIdAndMasksALoginIdentifier()
    {
        Assert.Equal("a…@acme.test", AlertCopySender.Masked("target", "ana@acme.test"));
        Assert.Equal("o…", AlertCopySender.Masked("target", "operator"));
        Assert.Equal("203.0.113.7", AlertCopySender.Masked("address", "203.0.113.7"));
        Assert.Equal("0198f0c2-0000-7000-8000-000000000001", AlertCopySender.Masked("principal", "0198f0c2-0000-7000-8000-000000000001"));
    }

    [Fact]
    [Proves("ADR-0056")]
    public void TheOperatorsAlertingGetsAnOpenAlertWithoutAnEndAndAResolvedOneWithItsEnd()
    {
        var raised = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        var alert = new Alert("acme", "permission_denials", Denials.Title, AlertKinds.Threshold, AlertSeverity.High, "principal", "someone", 120, 100, 10, raised, raised, raised, null, "/audit?x", "/api/v1/audit-events?x", Denials.Runbook);

        var open = AlertmanagerForwarder.Posted(alert);
        alert.Resolve(Guid.NewGuid(), raised.AddMinutes(30), "A test of the import, run by the integration team.");
        var resolved = AlertmanagerForwarder.Posted(alert);

        Assert.Equal(("permission_denials", "acme", "high"), (open.Labels["alertname"], open.Labels["tenant"], open.Labels["severity"]));
        Assert.Null(open.EndsAt);
        Assert.Equal(Denials.Runbook, open.Annotations["runbook"]);
        Assert.Equal("/audit?x", open.Annotations["link"]);
        Assert.Equal(raised.AddMinutes(30), resolved.EndsAt);
        var json = JsonSerializer.Serialize(resolved);
        Assert.Contains("\"startsAt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"endsAt\"", json, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0056")]
    public async Task AUnitSendsEachChangedAlertOnceAndTheControlPlaneKeepsOnlyTheNewestRevision()
    {
        var unit = await fixture.NewTenantAsync();
        var (time, start) = RuleEvaluationTests.Clock();
        await unit.WriteAsync(time, start, new AuditEvent("identity.login_locked", AuditOutcome.Success, Guid.Empty, "ana@acme.test"));
        await unit.EvaluateAsync(time, start.AddMinutes(1));

        await using (var provider = unit.Build(time))
        await using (var scope = provider.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<AlertCopySender>();
            Assert.Equal(1, await sender.SendAsync(Cancel));
            Assert.Equal(0, await sender.SendAsync(Cancel));
        }

        var (method, path, body) = Assert.Single(unit.ControlPlane.Received);
        var alert = Assert.Single(await RuleEvaluationTests.AlertsAsync(unit, "lockouts_by_login"));
        Assert.Equal(("PUT", $"/api/v1/alert-copies/{alert.Id:D}"), (method, path));
        var copy = JsonSerializer.Deserialize<AlertCopy>(body, ControlPlaneClient.Json)!;
        Assert.Equal("a…@acme.test", copy.GroupValue);
        Assert.Equal(1, copy.Revision);

        // The control plane keeps it, takes a newer revision, and ignores an older one that arrives late.
        var control = await fixture.NewTenantAsync(MonitoringScope.ControlPlane);
        await using var controlProvider = control.Build(time);
        await using var controlScope = controlProvider.CreateAsyncScope();
        var store = controlScope.ServiceProvider.GetRequiredService<IAlertCopies>();
        await store.KeepAsync("acme", copy with { Revision = 2, State = AlertStates.Resolved }, Cancel);
        await store.KeepAsync("acme", copy, Cancel);
        var kept = await controlScope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Alerts.AsNoTracking().SingleAsync(a => a.SourceId == alert.Id, Cancel);
        Assert.Equal((Alert.Copy, "acme", AlertStates.Resolved, 2), (kept.Origin, kept.Tenant, kept.State, kept.Revision));

        // A copy is acknowledged in its unit, where its events are.
        var refused = await controlScope.ServiceProvider.GetRequiredService<AlertService>().AcknowledgeAsync(kept.Id, null, Cancel);
        Assert.Equal(MonitoringErrors.AlertIsACopy, refused.Error.Code);
    }
}
