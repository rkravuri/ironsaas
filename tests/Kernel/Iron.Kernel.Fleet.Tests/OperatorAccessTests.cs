using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Fleet.Services.Steps;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Fleet.Tests;

/// <summary>
/// The operator's support and emergency access through the control plane and the fleet (S3 T5, TF-025, TF-026, TF-028,
/// ADR-0049): two operators, the 30-minute window, the unit's commands, the links handed over, and the review that
/// comes back.
/// </summary>
[Collection(FleetCollection.Name)]
public class OperatorAccessTests(FleetFixture fixture)
{
    private static readonly Guid Olga = Ids.New();
    private static readonly Guid Otto = Ids.New();

    [Fact]
    [Proves("TF-026 AC1", "TF-026 AC4", "ADR-0049")]
    public async Task ASecondOperatorApprovesTheFleetOpensTheAccountAndOnlyTheOneWhoAskedGetsTheLink()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, runtime, time, acting) = Start();
        await using var _ = provider;
        var slug = await ActiveTenantAsync(provider, "juliet");
        var harness = new ProvisioningWorkerHarness(provider);

        acting.Id = Olga;
        var requested = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().RequestAsync(slug, "The tenant's administrators are locked out", "INC-4711", 45, ct));
        var self = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().ApproveAsync(requested.Value.Id, ct));
        acting.Id = Otto;
        var approved = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().ApproveAsync(requested.Value.Id, ct));
        Assert.True(await harness.RunNextAsync());
        var notYours = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().FindAsync(requested.Value.Id, ct));
        acting.Id = Olga;
        var given = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().FindAsync(requested.Value.Id, ct));
        var open = runtime.Commands.Single(c => c.Command == "break-glass-open").Unit.Environment;
        var audited = await AuditAsync(provider, TenancyAuditEvents.BreakGlassApproved, slug);

        Assert.Equal(BreakGlassStatus.Requested, requested.Value.Status);
        Assert.Equal(TenancyErrors.BreakGlassSameOperator, self.Error.Code);
        Assert.Equal(BreakGlassStatus.Active, approved.Value.Status);
        Assert.Equal(time.GetUtcNow().AddMinutes(45), approved.Value.AccessUntil);
        Assert.Equal((Olga.ToString("D"), Otto.ToString("D"), "INC-4711", "The tenant's administrators are locked out"), (open["BreakGlass__Operator"], open["BreakGlass__Approver"], open["BreakGlass__Ticket"], open["BreakGlass__Reason"]));
        Assert.Equal(BreakGlassAccountNames.LoginIdOf(requested.Value.Id), approved.Value.AccountLoginId);
        Assert.Equal(HandoverState.NotYours, notYours.Value.LinkState);
        Assert.Equal((HandoverState.Given, "break_glass"), (given.Value.LinkState, given.Value.Link?.Kind));
        Assert.StartsWith("/sign-in-link#", given.Value.Link!.LinkPath, StringComparison.Ordinal);
        Assert.Equal((Otto, AuditSeverity.High), (audited.ActorId, audited.Severity));
    }

    [Fact]
    [Proves("TF-026 AC2", "ADR-0049")]
    public async Task ARequestNobodyApprovesWithinThirtyMinutesExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, runtime, time, acting) = Start();
        await using var _ = provider;
        var slug = await ActiveTenantAsync(provider, "kilo");

        acting.Id = Olga;
        var first = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().RequestAsync(slug, "Locked out", "INC-1", null, ct));
        var second = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().RequestAsync(slug, "Locked out again", "INC-2", null, ct));
        time.Advance(BreakGlassAccess.ApprovalWindow - TimeSpan.FromSeconds(1));
        await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().ReconcileAsync(ct));
        var stillWaiting = await StatusesAsync(provider, slug);
        time.Advance(TimeSpan.FromSeconds(1));
        await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().ReconcileAsync(ct));
        acting.Id = Otto;
        var late = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().ApproveAsync(first.Value.Id, ct));
        var states = await StatusesAsync(provider, slug);

        Assert.Equal([BreakGlassStatus.Requested, BreakGlassStatus.Requested], stillWaiting);
        Assert.Equal(TenancyErrors.BreakGlassNotRequested, late.Error.Code);
        Assert.Equal([BreakGlassStatus.Expired, BreakGlassStatus.Expired], states);
        Assert.DoesNotContain(runtime.Commands, c => c.Command == "break-glass-open");
        Assert.Equal("no second operator approved within 30 minutes", (await AuditAsync(provider, TenancyAuditEvents.BreakGlassExpired, slug)).Reason);
        Assert.True(second.IsSuccess);
    }

    [Fact]
    [Proves("TF-026 AC3", "ADR-0049")]
    public async Task AtItsTimeLimitTheAccountClosesAndTheReviewListsEveryActionOfIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, runtime, time, acting) = Start();
        await using var _ = provider;
        var slug = await ActiveTenantAsync(provider, "lima");
        var harness = new ProvisioningWorkerHarness(provider);
        acting.Id = Olga;
        var requested = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().RequestAsync(slug, "Repair a broken role", "INC-9", 30, ct));
        acting.Id = Otto;
        Assert.True((await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().ApproveAsync(requested.Value.Id, ct))).IsSuccess);
        Assert.True(await harness.RunNextAsync());

        time.Advance(TimeSpan.FromMinutes(31));
        acting.Id = SystemPrincipal.SystemId;
        await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().ReconcileAsync(ct));
        Assert.True(await harness.RunNextAsync());
        await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().ReconcileAsync(ct));
        var view = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().FindAsync(requested.Value.Id, ct));
        var close = runtime.Commands.Single(c => c.Command == "break-glass-close").Unit.Environment;
        var review = JsonDocument.Parse(view.Value.Access.Review!).RootElement.EnumerateArray().Select(a => a.GetProperty("event_type").GetString()!).ToList();
        var ended = await AuditAsync(provider, TenancyAuditEvents.BreakGlassEnded, slug);

        Assert.Equal((BreakGlassStatus.Ended, "time limit reached", 2), (view.Value.Access.Status, view.Value.Access.EndReason, view.Value.Access.ReviewActions));
        Assert.Equal((requested.Value.AccountLoginId, "time limit reached"), (close["BreakGlass__Account"], close["BreakGlass__Reason"]));
        Assert.Equal(RecordingRuntime.ReviewEvents, review);
        Assert.Equal("2", JsonDocument.Parse(ended.Details!).RootElement.GetProperty("actions").GetString());
    }

    [Fact]
    [Proves("TF-026 AC3", "ADR-0049")]
    public async Task AnOperatorEndsAnAccessEarlyAndItsReviewFollows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, _, _, acting) = Start();
        await using var _ = provider;
        var slug = await ActiveTenantAsync(provider, "mike");
        var harness = new ProvisioningWorkerHarness(provider);
        acting.Id = Olga;
        var requested = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().RequestAsync(slug, "Restore an SSO setting", "INC-10", null, ct));
        acting.Id = Otto;
        Assert.True((await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().ApproveAsync(requested.Value.Id, ct))).IsSuccess);
        Assert.True(await harness.RunNextAsync());

        acting.Id = Olga;
        var ending = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().EndAsync(requested.Value.Id, ct));
        Assert.True(await harness.RunNextAsync());
        await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().ReconcileAsync(ct));
        var view = await WithAsync(provider, s => s.GetRequiredService<BreakGlassService>().FindAsync(requested.Value.Id, ct));

        Assert.Equal(BreakGlassStatus.Ending, ending.Value.Status);
        Assert.Equal((BreakGlassStatus.Ended, $"ended early by {Olga:D}"), (view.Value.Access.Status, view.Value.Access.EndReason));
        Assert.NotNull(view.Value.Access.Review);
    }

    [Fact]
    [Proves("TF-025 AC1", "ADR-0049")]
    public async Task TheOperatorsImpersonationNeedsAReasonAndHandsTheLinkToTheOperatorAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, runtime, _, acting) = Start();
        await using var _ = provider;
        var slug = await ActiveTenantAsync(provider, "november");
        var harness = new ProvisioningWorkerHarness(provider);
        acting.Id = Olga;

        var withoutReason = await WithAsync(provider, s => s.GetRequiredService<OperatorImpersonation>().RequestAsync(slug, "pat@november.test", "  ", 30, ct));
        var requested = await WithAsync(provider, s => s.GetRequiredService<OperatorImpersonation>().RequestAsync(slug, "Pat@November.test", "Support ticket 7", 30, ct));
        Assert.True(await harness.RunNextAsync());
        var command = runtime.Commands.Single(c => c.Command == "impersonate").Unit.Environment;
        var job = await WithAsync(provider, s => s.GetRequiredService<IProvisioningJobs>().FindAsync(requested.Value.Id, ct));
        var someoneElse = await WithAsync(provider, s => s.GetRequiredService<IJobHandovers>().TakeAsync(requested.Value.Id, Otto, ct));
        var asked = await WithAsync(provider, s => s.GetRequiredService<IJobHandovers>().TakeAsync(requested.Value.Id, Olga, ct));
        var audited = await AuditAsync(provider, TenancyAuditEvents.ImpersonationRequested, slug);

        Assert.Equal(TenancyErrors.ImpersonationReasonRequired, withoutReason.Error.Code);
        Assert.Equal(("pat@november.test", Olga.ToString("D"), "Support ticket 7", "30"), (command["Impersonation__LoginId"], command["Impersonation__OperatorId"], command["Impersonation__Reason"], command["Impersonation__Minutes"]));
        Assert.Equal((ProvisioningStatus.Completed, StepNames.Impersonate), (job!.Status, job.Steps.Single().Name));
        Assert.Equal(HandoverState.NotYours, someoneElse.State);
        Assert.Equal((HandoverState.Given, "impersonation"), (asked.State, asked.Item?.Kind));
        Assert.Equal(("Support ticket 7", AuditSeverity.High), (audited.Reason, audited.Severity));
    }

    [Fact]
    [Proves("ADR-0060", "ADR-0049")]
    public async Task TheOperatorSearchesAndReplaysATenantsEventsThroughTheUnitsCommandWithAReasonInTheTrail()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, runtime, _, acting) = Start();
        await using var _ = provider;
        var slug = await ActiveTenantAsync(provider, "papa");
        var harness = new ProvisioningWorkerHarness(provider);
        acting.Id = Olga;
        var record = Ids.New();
        var dead = Ids.New();

        var searched = await WithAsync(provider, s => s.GetRequiredService<OperatorEvents>().SearchAsync(slug, new OperatorEventSearch("identity.person.deactivated", record, null, DeadOnly: true), ct));
        Assert.True(await harness.RunNextAsync());
        var search = runtime.Commands.Single(c => c.Command == "events").Unit.Environment;
        var searchJob = await WithAsync(provider, s => s.GetRequiredService<IProvisioningJobs>().FindAsync(searched.Value.Id, ct));

        var withoutReason = await WithAsync(provider, s => s.GetRequiredService<OperatorEvents>().ReplayAsync(slug, new OperatorEventReplay("authz.end_delegations_of_deactivated", [dead], null, null, " "), ct));
        var replayed = await WithAsync(provider, s => s.GetRequiredService<OperatorEvents>().ReplayAsync(slug, new OperatorEventReplay("authz.end_delegations_of_deactivated", [dead], null, null, "Ticket 12: the delegation stayed open"), ct));
        Assert.True(await harness.RunNextAsync());
        var replay = runtime.Commands.Last(c => c.Command == "events").Unit.Environment;
        var replayJob = await WithAsync(provider, s => s.GetRequiredService<IProvisioningJobs>().FindAsync(replayed.Value.Id, ct));
        var audited = await AuditAsync(provider, TenancyAuditEvents.EventsReplayRequested, slug);

        Assert.Equal(("search", "identity.person.deactivated", record.ToString("D"), "dead"), (search["Events__Command__Action"], search["Events__Command__Type"], search["Events__Command__RecordId"], search["Events__Command__Status"]));
        Assert.Equal(JobKind.Events, searchJob!.Kind);
        Assert.Equal((ProvisioningStatus.Completed, StepNames.Events), (searchJob.Status, searchJob.Steps.Single().Name));
        Assert.StartsWith("Searched: 0 event(s)", searchJob.Steps.Single().Output, StringComparison.Ordinal);
        Assert.Equal(TenancyErrors.EventsReasonRequired, withoutReason.Error.Code);
        Assert.Equal(("replay", "authz.end_delegations_of_deactivated", dead.ToString("D"), "Ticket 12: the delegation stayed open"), (replay["Events__Command__Action"], replay["Events__Command__Subscriber"], replay["Events__Command__Ids"], replay["Events__Command__Reason"]));
        Assert.False(string.IsNullOrEmpty(replay["Events__Command__Operator"]));
        Assert.StartsWith("Replayed 1 event(s)", replayJob!.Steps.Single().Output, StringComparison.Ordinal);
        Assert.Equal((Olga, "Ticket 12: the delegation stayed open", AuditSeverity.High), (audited.ActorId, audited.Reason, audited.Severity));
        using var details = JsonDocument.Parse(audited.Details!);
        Assert.Equal(replayed.Value.Id.ToString("D"), details.RootElement.GetProperty("job_id").GetString());
        Assert.DoesNotContain(details.RootElement.EnumerateObject(), property => property.Name == "reason"); // the reason sits in the event's own field, once
    }

    [Fact]
    [Proves("ADR-0061", "ADR-0049")]
    public async Task TheOperatorListsAndPausesATenantsJobsThroughTheUnitsCommandWithAReasonInTheTrail()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, runtime, _, acting) = Start();
        await using var _ = provider;
        var slug = await ActiveTenantAsync(provider, "quebec");
        var harness = new ProvisioningWorkerHarness(provider);
        acting.Id = Olga;

        var listed = await WithAsync(provider, s => s.GetRequiredService<OperatorJobs>().ListAsync(slug, null, ct));
        Assert.True(await harness.RunNextAsync());
        var list = runtime.Commands.Single(c => c.Command == "jobs").Unit.Environment;
        var listJob = await WithAsync(provider, s => s.GetRequiredService<IProvisioningJobs>().FindAsync(listed.Value.Id, ct));

        var withoutReason = await WithAsync(provider, s => s.GetRequiredService<OperatorJobs>().ActAsync(slug, new OperatorJobAction(OperatorJobs.Pause, "audit.retention", null, "  "), ct));
        var paused = await WithAsync(provider, s => s.GetRequiredService<OperatorJobs>().ActAsync(slug, new OperatorJobAction(OperatorJobs.Pause, "audit.retention", null, "Ticket 14: the archive is being moved"), ct));
        Assert.True(await harness.RunNextAsync());
        var pause = runtime.Commands.Last(c => c.Command == "jobs").Unit.Environment;
        var pauseJob = await WithAsync(provider, s => s.GetRequiredService<IProvisioningJobs>().FindAsync(paused.Value.Id, ct));
        var audited = await AuditAsync(provider, TenancyAuditEvents.JobsActionRequested, slug);

        Assert.Equal("list", list["Jobs__Command__Action"]);
        Assert.False(list.ContainsKey("Jobs__Command__Reason"));
        Assert.Equal("Warning", list["Logging__LogLevel__Default"]); // the answer is the command's output, without the host's information lines
        Assert.Equal((JobKind.Jobs, ProvisioningStatus.Completed, StepNames.Jobs), (listJob!.Kind, listJob.Status, listJob.Steps.Single().Name));
        Assert.StartsWith("audit.retention active", listJob.Steps.Single().Output, StringComparison.Ordinal);
        Assert.Equal(TenancyErrors.JobsReasonRequired, withoutReason.Error.Code);
        Assert.Equal(("pause", "audit.retention", "Ticket 14: the archive is being moved"), (pause["Jobs__Command__Action"], pause["Jobs__Command__Job"], pause["Jobs__Command__Reason"]));
        Assert.False(string.IsNullOrEmpty(pause["Jobs__Command__Operator"]));
        Assert.StartsWith("Paused audit.retention", pauseJob!.Steps.Single().Output, StringComparison.Ordinal);
        Assert.Equal((Olga, "Ticket 14: the archive is being moved", AuditSeverity.High), (audited.ActorId, audited.Reason, audited.Severity));
        using var details = JsonDocument.Parse(audited.Details!);
        Assert.Equal(paused.Value.Id.ToString("D"), details.RootElement.GetProperty("job_id").GetString());
    }

    [Fact]
    [Proves("TF-028 AC1", "ADR-0049")]
    public async Task TheOperatorPublishesThePlatformsDocumentsWhichEveryUnitMayRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, _, _, acting) = Start();
        await using var _ = provider;
        var slug = await ActiveTenantAsync(provider, "oscar");
        acting.Id = Olga;

        var unknown = await WithAsync(provider, s => s.GetRequiredService<PlatformDocumentService>().PublishAsync("cookies", "Cookies", "None.", ct));
        var first = await WithAsync(provider, s => s.GetRequiredService<PlatformDocumentService>().PublishAsync(PlatformDocumentKeys.TermsOfService, "Terms of service", "Version one.", ct));
        var second = await WithAsync(provider, s => s.GetRequiredService<PlatformDocumentService>().PublishAsync(PlatformDocumentKeys.TermsOfService, "Terms of service", "Version two.", ct));
        var notice = await WithAsync(provider, s => s.GetRequiredService<PlatformDocumentService>().PublishAsync(PlatformDocumentKeys.PrivacyNotice, "Privacy notice", "Notice.", ct));

        // The unit's own token reads the platform's documents, as it reads the platform's flags.
        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        var token = (await root.ReadAsync($"tenants/{slug}/fleet", ct))![SecretsStep.UnitTokenKey];
        using var unit = new OpenBaoSecretStore(fixture.StoreOptions(token));
        var stored = PlatformDocumentsStore.Parse((await unit.ReadAsync(PlatformDocumentsStore.Path, ct))![PlatformDocumentsStore.DocumentKey]);

        Assert.Equal(TenancyErrors.DocumentKeyUnknown, unknown.Error.Code);
        Assert.Equal((1, 2, 1), (first.Value.Number, second.Value.Number, notice.Value.Number));
        Assert.Equal([(PlatformDocumentKeys.PrivacyNotice, 1), (PlatformDocumentKeys.TermsOfService, 2)], stored.Select(d => (d.Key, d.Version)));
        Assert.Equal("Version two.", stored.Single(d => d.Key == PlatformDocumentKeys.TermsOfService).Body);
    }

    private (ServiceProvider Provider, RecordingRuntime Runtime, FakeTimeProvider Time, ActingOperator Acting) Start()
    {
        var runtime = new RecordingRuntime();
        var time = FleetFixture.Clock();
        var acting = new ActingOperator();
        var provider = fixture.BuildServices(time, runtime, services => services.Replace(ServiceDescriptor.Singleton<ICurrentPrincipal>(acting)));
        return (provider, runtime, time, acting);
    }

    /// <summary>A tenant provisioned by the fleet, active.</summary>
    private static async Task<string> ActiveTenantAsync(ServiceProvider provider, string prefix)
    {
        var slug = $"{prefix}-{Ids.New():N}"[..20];
        await using (var scope = provider.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
            Assert.True((await registry.CreateAsync(new NewTenant(slug, prefix, [], $"admin@{prefix}.test", "Admin", SizeClass.Small, null, null), TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await registry.RequestProvisioningAsync(slug, TestContext.Current.CancellationToken)).IsSuccess);
        }

        Assert.True(await new ProvisioningWorkerHarness(provider).RunNextAsync());
        return slug;
    }

    private static Task<List<BreakGlassStatus>> StatusesAsync(ServiceProvider provider, string slug) =>
        WithAsync(provider, s => s.GetRequiredService<TenancyDbContext>().BreakGlassAccesses.AsNoTracking().Where(a => a.TenantSlug == slug).Select(a => a.Status).ToListAsync(TestContext.Current.CancellationToken));

    private static async Task<T> WithAsync<T>(ServiceProvider provider, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = provider.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private static Task<AuditEventRecord> AuditAsync(ServiceProvider provider, string eventType, string target) =>
        WithAsync(provider, s => s.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.EventType == eventType && e.Target == target).OrderByDescending(e => e.CreatedAt).FirstAsync(TestContext.Current.CancellationToken));

    /// <summary>The operator acting now, as the control plane's request principal would be.</summary>
    private sealed class ActingOperator : ICurrentPrincipal
    {
        public Guid Id { get; set; } = SystemPrincipal.SystemId;
    }
}
