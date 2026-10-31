using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Monitoring.Data;
using Iron.Kernel.Monitoring.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Monitoring.Tests;

/// <summary>The rules as the worker runs them every minute, over the audit trail a tenant's modules wrote (TF-079, ADR-0056).</summary>
[Collection(MonitoringCollection.Name)]
public class RuleEvaluationTests(MonitoringFixture fixture)
{
    private const string Denied = "authz.permission_denied";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-079 AC1", "ADR-0056")]
    public async Task OneHundredDenialsFromOnePrincipalInTenMinutesRaiseAnAlertAndNinetyNineDoNot()
    {
        var tenant = await fixture.NewTenantAsync();
        var (time, start) = Clock();
        var many = Guid.NewGuid();
        var fewer = Guid.NewGuid();
        await DenialsAsync(tenant, many, start, 100, TimeSpan.FromSeconds(5));
        await DenialsAsync(tenant, fewer, start, 99, TimeSpan.FromSeconds(5));

        var pass = await tenant.EvaluateAsync(time, start.AddMinutes(9));

        var alert = Assert.Single(await AlertsAsync(tenant, "permission_denials"));
        Assert.Equal(1, pass.Raised);
        Assert.Equal(many.ToString(), alert.GroupValue);
        Assert.Equal("principal", alert.GroupKind);
        Assert.Equal(100, alert.Count);
        Assert.Equal((100, 10), (alert.Threshold, alert.WindowMinutes));
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.Equal(AlertStates.Open, alert.State);
        Assert.Equal((start, start.AddSeconds(495)), (alert.FirstEventAt, alert.LastEventAt));

        // The alert is itself on the record, raised by the system.
        var raised = Assert.Single(await EventsAsync(tenant, MonitoringAuditEvents.AlertRaised));
        Assert.Equal(alert.Id, raised.RecordId);
        Assert.Equal(SystemPrincipal.SystemId, raised.ActorId);
    }

    [Fact]
    [Proves("TF-079 AC1", "ADR-0056")]
    public async Task OneBurstRaisesOneAlertUnderTheCooldownAndABurstAfterItRaisesAnother()
    {
        var tenant = await fixture.NewTenantAsync();
        var (time, start) = Clock();
        var person = Guid.NewGuid();
        await DenialsAsync(tenant, person, start, 100, TimeSpan.FromSeconds(2));
        await tenant.EvaluateAsync(time, start.AddMinutes(4));

        // The burst goes on over the next minutes: each pass counts it into the same alert.
        await DenialsAsync(tenant, person, start.AddMinutes(5), 150, TimeSpan.FromSeconds(1));
        await tenant.EvaluateAsync(time, start.AddMinutes(8));
        await tenant.EvaluateAsync(time, start.AddMinutes(9));

        var one = Assert.Single(await AlertsAsync(tenant, "permission_denials"));
        Assert.Equal(250, one.Count);
        Assert.True(one.Revision >= 2);

        // An hour on, the cool-down is over: another hundred is another alert, counting only what came after the first.
        await DenialsAsync(tenant, person, start.AddMinutes(70), 100, TimeSpan.FromSeconds(1));
        await tenant.EvaluateAsync(time, start.AddMinutes(73));

        var alerts = await AlertsAsync(tenant, "permission_denials");
        Assert.Equal(2, alerts.Count);
        Assert.Equal(100, alerts.Single(a => a.Id != one.Id).Count);
    }

    [Fact]
    [Proves("TF-079 AC3", "ADR-0056")]
    public async Task AnExportOfMoreThanTenThousandRecordsRaisesAnAlertBesideItsOwnAuditEvent()
    {
        var tenant = await fixture.NewTenantAsync();
        var (time, start) = Clock();
        var exporter = Guid.NewGuid();
        var modest = Guid.NewGuid();
        await tenant.WriteAsync(time, start, new AuditEvent(AuditEvents.EventsExported, AuditOutcome.Success, exporter, "audit trail", Rows(10_001)));
        await tenant.WriteAsync(time, start.AddSeconds(1), new AuditEvent("authz.roles_exported", AuditOutcome.Success, modest, "roles", Rows(10_000)));

        await tenant.EvaluateAsync(time, start.AddMinutes(1));

        var alert = Assert.Single(await AlertsAsync(tenant, "mass_exports"));
        Assert.Equal(exporter.ToString(), alert.GroupValue);
        Assert.Single(await EventsAsync(tenant, AuditEvents.EventsExported));
    }

    [Fact]
    [Proves("TF-079 AC4", "ADR-0056")]
    public async Task AMisconfiguredRuleIsReportedAsFailingWithAnAlertOfItsOwnAndTheOtherRulesStillRun()
    {
        var tenant = await fixture.NewTenantAsync();
        var (time, start) = Clock();
        tenant.Configuration["Monitoring:Rules:permission_denials:Threshold"] = "a hundred";
        tenant.Configuration["Monitoring:Rules:mass_exports:Treshold"] = "5";
        tenant.Configuration["Monitoring:Rules:permision_denials:Threshold"] = "5";
        await DenialsAsync(tenant, Guid.NewGuid(), start, 100, TimeSpan.FromSeconds(1));
        await tenant.WriteAsync(time, start.AddMinutes(2), new AuditEvent("identity.impersonation_started", AuditOutcome.Success, Guid.NewGuid(), "ana@acme.test"));

        await tenant.EvaluateAsync(time, start.AddMinutes(3));
        await tenant.EvaluateAsync(time, start.AddMinutes(4));

        var states = await StatesAsync(tenant);
        Assert.Equal(AlertRuleState.Failing, states["permission_denials"].State);
        Assert.Contains("Monitoring:Rules:permission_denials:Threshold is 'a hundred'", states["permission_denials"].Reason, StringComparison.Ordinal);
        Assert.Contains("Treshold is no value of a rule", states["mass_exports"].Reason, StringComparison.Ordinal);
        Assert.Contains("names no rule", states["permision_denials"].Reason, StringComparison.Ordinal);
        Assert.Equal(AlertRuleState.Running, states["impersonations"].State);
        Assert.Single(await AlertsAsync(tenant, "impersonations"));
        Assert.Empty(await AlertsAsync(tenant, "permission_denials"));

        // Each failing rule raised one alert, the first time only, and is on the record.
        var failing = await FailingAsync(tenant);
        Assert.Equal(["mass_exports", "permision_denials", "permission_denials"], failing.Select(a => a.Rule).Order(StringComparer.Ordinal));
        Assert.All(failing, a => Assert.Equal(AlertRules.FailingRunbook, a.Runbook));
        Assert.Equal(3, (await EventsAsync(tenant, MonitoringAuditEvents.RuleFailing)).Count);

        // Corrected, it runs again, and its own events show it.
        tenant.Configuration.Remove("Monitoring:Rules:permission_denials:Threshold");
        await tenant.EvaluateAsync(time, start.AddMinutes(5));
        Assert.Equal(AlertRuleState.Running, (await StatesAsync(tenant))["permission_denials"].State);
        Assert.Single(await AlertsAsync(tenant, "permission_denials"));
        Assert.Single(await EventsAsync(tenant, MonitoringAuditEvents.RuleRecovered));
    }

    [Fact]
    [Proves("TF-079 AC4", "ADR-0056")]
    public async Task ARuleWhoseEvaluationThrowsIsFailingWithTheReasonAndTheOtherRulesStillRun()
    {
        var tenant = await fixture.NewTenantAsync();
        var (time, start) = Clock();

        await tenant.EvaluateAsync(time, start, more: services =>
            services.AddScoped<IAuditCounts>(provider => new ThrowingCounts(ActivatorUtilities.CreateInstance<AuditCounter>(provider), "lockouts_by_address")));

        var states = await StatesAsync(tenant);
        Assert.Equal(AlertRuleState.Failing, states["lockouts_by_address"].State);
        Assert.Contains("Its evaluation failed: the trail cannot be read now", states["lockouts_by_address"].Reason, StringComparison.Ordinal);
        Assert.Equal(AlertRuleState.Running, states["sign_in_failures_by_address"].State);
    }

    [Fact]
    [Proves("TF-079 AC4", "ADR-0056")]
    public async Task APassAfterAGapReportsTheGapAndARuleForEachEventCatchesUpOnIt()
    {
        var tenant = await fixture.NewTenantAsync();
        var (time, start) = Clock();
        await tenant.EvaluateAsync(time, start);

        // The worker was down for an hour; someone used break-glass in the middle of it.
        await tenant.WriteAsync(time, start.AddMinutes(30), new AuditEvent("identity.break_glass_opened", AuditOutcome.Success, SystemPrincipal.SystemId, "break-glass-1@break-glass.invalid"));
        await tenant.EvaluateAsync(time, start.AddMinutes(60));
        await tenant.EvaluateAsync(time, start.AddMinutes(61));

        var gap = Assert.Single(await FailingAsync(tenant));
        Assert.Equal(RuleEvaluator.Evaluation, gap.Rule);
        Assert.Contains("did not run from", gap.Reason, StringComparison.Ordinal);
        var breakGlass = Assert.Single(await AlertsAsync(tenant, "break_glass"));
        Assert.Equal(AlertSeverity.Critical, breakGlass.Severity);
    }

    [Fact]
    [Proves("TF-079 AC1", "ADR-0056")]
    public async Task EachImpersonationRaisesAnAlertAtOnceAndNoEventCountsTwice()
    {
        var tenant = await fixture.NewTenantAsync();
        var (time, start) = Clock();
        var impersonator = Guid.NewGuid();
        await tenant.WriteAsync(time, start, new AuditEvent("identity.impersonation_started", AuditOutcome.Success, impersonator, "ana@acme.test"));
        await tenant.EvaluateAsync(time, start.AddMinutes(1));
        await tenant.EvaluateAsync(time, start.AddMinutes(2));
        await tenant.WriteAsync(time, start.AddMinutes(3), new AuditEvent("identity.impersonation_started", AuditOutcome.Success, impersonator, "ben@acme.test"));
        await tenant.EvaluateAsync(time, start.AddMinutes(4));

        var alerts = await AlertsAsync(tenant, "impersonations");
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(1, a.Count));
    }

    [Fact]
    [Proves("TF-123 AC2", "TF-123 AC3", "ADR-0061")]
    public async Task AJobsDeadLetterAndAMissedScheduleEachRaiseAnAlertAtOnceAndTenDeadLettersInAnHourRaiseAnother()
    {
        var tenant = await fixture.NewTenantAsync();
        var (time, start) = Clock();
        await tenant.WriteAsync(time, start, new AuditEvent("jobs.run_dead_lettered", AuditOutcome.Failure, Guid.Empty, "probe.flaky"));
        await tenant.WriteAsync(time, start, new AuditEvent("jobs.schedule_missed", AuditOutcome.Failure, Guid.Empty, "audit.retention"));
        for (var i = 0; i < 9; i++)
        {
            await tenant.WriteAsync(time, start.AddMinutes(1), new AuditEvent(i % 2 == 0 ? "jobs.run_dead_lettered" : "events.delivery_dead_lettered", AuditOutcome.Failure, Guid.Empty, $"subscriber.{i}"));
        }

        await tenant.EvaluateAsync(time, start.AddMinutes(2));

        Assert.Equal("probe.flaky", Assert.Single(await AlertsAsync(tenant, "job_dead_letters"), a => a.GroupValue == "probe.flaky").GroupValue);
        Assert.Equal("audit.retention", Assert.Single(await AlertsAsync(tenant, "job_schedule_missed")).GroupValue);
        var growing = Assert.Single(await AlertsAsync(tenant, "dead_letters_growing"));
        Assert.Equal(10, growing.Count);
    }

    /// <summary>Denials of one principal, one after another from <paramref name="from"/>, on a clock of their own.</summary>
    internal static async Task DenialsAsync(MonitoredTenant tenant, Guid principal, DateTimeOffset from, int count, TimeSpan apart)
    {
        var time = new FakeTimeProvider(from);
        await using var provider = tenant.Build(time);
        for (var i = 0; i < count; i++)
        {
            time.SetUtcNow(from + (apart * i));
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEvent(Denied, AuditOutcome.Denied, principal, "GET /api/v1/audit-events"), Cancel);
        }
    }

    internal static (FakeTimeProvider Time, DateTimeOffset Start) Clock()
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
        return (new FakeTimeProvider(start), start);
    }

    internal static async Task<List<Alert>> AlertsAsync(MonitoredTenant tenant, string rule)
    {
        await using var provider = tenant.Build(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Alerts.AsNoTracking()
            .Where(a => a.Rule == rule && a.Kind == AlertKinds.Threshold).OrderBy(a => a.RaisedAt).ToListAsync(Cancel);
    }

    private static Dictionary<string, string> Rows(int rows) => new(StringComparer.Ordinal) { [AuditDetails.Rows] = rows.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    private static async Task<List<Alert>> FailingAsync(MonitoredTenant tenant)
    {
        await using var provider = tenant.Build(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Alerts.AsNoTracking().Where(a => a.Kind == AlertKinds.Failing).ToListAsync(Cancel);
    }

    private static async Task<Dictionary<string, AlertRuleState>> StatesAsync(MonitoredTenant tenant)
    {
        await using var provider = tenant.Build(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().RuleStates.AsNoTracking().ToDictionaryAsync(s => s.Rule, StringComparer.Ordinal, Cancel);
    }

    private static async Task<List<AuditEventRecord>> EventsAsync(MonitoredTenant tenant, string type)
    {
        await using var provider = tenant.Build(new FakeTimeProvider());
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.EventType == type).ToListAsync(Cancel);
    }

    /// <summary>The trail as it reads for every rule but one, which cannot read it.</summary>
    private sealed class ThrowingCounts(IAuditCounts inner, string failing) : IAuditCounts
    {
        public Task<IReadOnlyList<AuditGroupCount>> CountAsync(AuditCountQuery query, CancellationToken cancellationToken)
        {
            var rule = AlertRules.All.Single(r => r.Name == failing);
            return query.Matches.SequenceEqual(rule.Matches) && query.GroupBy == rule.GroupBy
                ? throw new InvalidOperationException("the trail cannot be read now")
                : inner.CountAsync(query, cancellationToken);
        }
    }
}
