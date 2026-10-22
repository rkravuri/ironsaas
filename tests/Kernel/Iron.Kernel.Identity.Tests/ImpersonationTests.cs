using System.Net;
using System.Text.Json;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Handover;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iron.Kernel.Identity.Tests;

/// <summary>Impersonation within the tenant and by the operator (S3 T5, TF-025, ADR-0049).</summary>
[Collection(IdentityCollection.Name)]
public class ImpersonationTests(IdentityFixture fixture)
{
    private const string ImpersonationCookie = "iron.impersonation";

    [Fact]
    [Proves("TF-025 AC1")]
    public async Task AnImpersonationWithoutAReasonIsRefused()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (_, admin) = await host.AdministratorAsync();
        var person = await host.PersonAsync();

        using var none = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/impersonation", admin, new { minutes = 30 });
        using var blank = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/impersonation", admin, new { reason = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Null(UnitHost.Cookie(blank, ImpersonationCookie));
        Assert.DoesNotContain(await host.SessionsAsync(person.Id), s => s.IsImpersonation);
    }

    [Fact]
    [Proves("TF-025 AC4", "ADR-0046", "ADR-0049")]
    public async Task OnlySomeoneWhosePermissionsTheImpersonatorHoldsCanBeSignedInAs()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var helpdesk = await host.RoleAsync([IdentityPermissions.ImpersonationStart.Name, IdentityPermissions.UsersRead.Name, AuthorizationPermissions.RolesRead.Name]);
        var agent = await host.PersonWithRoleAsync(helpdesk, displayName: "Helen Helpdesk");
        var session = await host.SessionOfAsync(agent);
        var reader = await host.PersonWithRoleAsync(SystemRoles.ReadOnly);
        var clerk = await host.PersonAsync();
        var (administrator, _) = await host.AdministratorAsync();

        using var stronger = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{administrator.Id}/impersonation", session, new { reason = "Ticket 41" });
        using var wider = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{reader.Id}/impersonation", session, new { reason = "Ticket 42" });
        using var self = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{agent.Id}/impersonation", session, new { reason = "Ticket 43" });
        using var allowed = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{clerk.Id}/impersonation", session, new { reason = "Ticket 44" });

        Assert.Equal(IdentityErrors.ImpersonationEscalation, await UnitHost.ProblemAsync(stronger, HttpStatusCode.Forbidden));
        Assert.Equal(IdentityErrors.ImpersonationEscalation, await UnitHost.ProblemAsync(wider, HttpStatusCode.Forbidden));
        Assert.Equal(IdentityErrors.ImpersonationSelf, await UnitHost.ProblemAsync(self, HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.NotNull(UnitHost.Cookie(allowed, ImpersonationCookie));
    }

    [Fact]
    [Proves("TF-025 AC2", "TF-025 AC6", "TF-248 AC1", "ADR-0049")]
    public async Task EveryActionNamesBothPeopleAndThePersonSeesWhoWhenAndWhy()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (administrator, admin) = await host.AdministratorAsync("Ada Admin");
        var person = await host.PersonAsync(displayName: "Pat Person");
        var personSession = await host.SessionOfAsync(person);

        host.Time.Advance(TimeSpan.FromMinutes(6));
        using var stale = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/impersonation", admin, new { reason = "Their invoices do not show" });
        admin = await host.SessionOfAsync(administrator);
        using var started = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/impersonation", admin, new { reason = "Their invoices do not show", minutes = 30 });
        var both = $"{admin}; {UnitHost.Cookie(started, ImpersonationCookie)}";
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", both);
        var meBody = await UnitHost.JsonAsync(me);
        var home = meBody.GetProperty("home_unit").GetProperty("id").GetGuid();
        using var change = await host.SendAsync(HttpMethod.Post, "/api/v1/me/working-unit", both, new { unit_id = home });
        using var ended = await host.SendAsync(HttpMethod.Post, "/api/v1/me/impersonation/end", both);
        using var audit = await host.SendAsync(HttpMethod.Get, $"/api/v1/audit-events?filter=impersonator_id:eq:{administrator.Id}&sort=created_at", admin);
        var rows = (await UnitHost.JsonAsync(audit)).GetProperty("data").EnumerateArray().ToList();
        var chosen = rows.Single(r => r.GetProperty("event_type").GetString() == IdentityAuditEvents.WorkingUnitChosen);
        using var activity = await host.SendAsync(HttpMethod.Get, "/api/v1/me/security-events", personSession);
        var events = (await UnitHost.JsonAsync(activity)).GetProperty("data").EnumerateArray().ToList();
        var start = events.Single(e => e.GetProperty("kind").GetString() == SecurityEventKinds.ImpersonationStarted);

        Assert.Equal(RecentAuthentication.ErrorCode, await UnitHost.ProblemAsync(stale, HttpStatusCode.Forbidden));
        Assert.Equal(person.Id, meBody.GetProperty("id").GetGuid());
        Assert.Equal(("Ada Admin", "Their invoices do not show", false), (meBody.GetProperty("impersonation").GetProperty("impersonator_name").GetString(), meBody.GetProperty("impersonation").GetProperty("reason").GetString(), meBody.GetProperty("impersonation").GetProperty("operator").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Equal((person.Id, "Pat Person", administrator.Id, "Ada Admin"), (chosen.GetProperty("actor_id").GetGuid(), chosen.GetProperty("actor_name").GetString(), chosen.GetProperty("impersonator_id").GetGuid(), chosen.GetProperty("impersonator_name").GetString()));
        Assert.Equal(("administrator", "Ada Admin", "Their invoices do not show"), (start.GetProperty("actor").GetString(), start.GetProperty("actor_name").GetString(), start.GetProperty("reason").GetString()));
        Assert.Contains(events, e => e.GetProperty("kind").GetString() == SecurityEventKinds.ImpersonationEnded);
    }

    [Fact]
    [Proves("TF-025 AC3", "ADR-0049")]
    public async Task AtItsTimeTheNextRequestEndsTheImpersonationAndRunsAsTheImpersonatorAgain()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (administrator, admin) = await host.AdministratorAsync();
        var person = await host.PersonAsync();
        using var started = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/impersonation", admin, new { reason = "Reproduce a reported problem" });
        var both = $"{admin}; {UnitHost.Cookie(started, ImpersonationCookie)}";

        // Two hours, the default and the most: the impersonator's own session idles out after an hour unless kept alive.
        host.Time.Advance(TimeSpan.FromMinutes(50));
        using var during = await host.SendAsync(HttpMethod.Get, "/api/v1/me", both);
        host.Time.Advance(TimeSpan.FromMinutes(50));
        using var later = await host.SendAsync(HttpMethod.Get, "/api/v1/me", both);
        host.Time.Advance(TimeSpan.FromMinutes(21));
        using var after = await host.SendAsync(HttpMethod.Get, "/api/v1/me", both);
        var afterBody = await UnitHost.JsonAsync(after);

        Assert.Equal(person.Id, (await UnitHost.JsonAsync(during)).GetProperty("id").GetGuid());
        Assert.Equal(person.Id, (await UnitHost.JsonAsync(later)).GetProperty("id").GetGuid());
        Assert.Equal(administrator.Id, afterBody.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, afterBody.GetProperty("impersonation").ValueKind);
        Assert.Equal("true", after.Headers.GetValues(SessionMiddleware.ImpersonationEndedHeader).Single());
        Assert.Contains(after.Headers.GetValues("Set-Cookie"), c => c.StartsWith(ImpersonationCookie + "=;", StringComparison.Ordinal));
        Assert.Equal(SessionEndReasons.ImpersonationEnded, (await host.SessionsAsync(person.Id)).Single(s => s.IsImpersonation).EndReason);
        Assert.Contains(await host.SecurityEventsAsync(person.Id), e => e.Kind == SecurityEventKinds.ImpersonationEnded && e.Actor == SecurityEventActors.System);
    }

    [Fact]
    [Proves("TF-025 AC5", "ADR-0049")]
    public async Task AnImpersonatorNeverStepsUpNorChangesThePersonsSignInFactorsPasswordOrSessions()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var (_, admin) = await host.AdministratorAsync();
        var person = await host.PersonWithRoleAsync(await host.RoleAsync([IdentityPermissions.UsersManage.Name, IdentityPermissions.UsersRead.Name, IdentityPermissions.ImpersonationStart.Name]));
        var colleague = await host.PersonAsync();
        using var started = await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{person.Id}/impersonation", admin, new { reason = "Check their screens" });
        var both = $"{admin}; {UnitHost.Cookie(started, ImpersonationCookie)}";

        var refusals = new List<(string, HttpResponseMessage)>
        {
            ("reauthenticate", await host.SendAsync(HttpMethod.Post, "/api/v1/me/reauthenticate", both, new { password = UnitHost.Password })),
            ("reauthentication", await host.SendAsync(HttpMethod.Post, "/api/v1/me/reauthentication", both)),
            ("password", await host.SendAsync(HttpMethod.Post, "/api/v1/me/password", both, new { current_password = UnitHost.Password, new_password = "a different passphrase", end_other_sessions = true })),
            ("factor", await host.SendAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app", both)),
            ("codes", await host.SendAsync(HttpMethod.Post, "/api/v1/me/recovery-codes", both)),
            ("sessions", await host.SendAsync(HttpMethod.Post, "/api/v1/me/sessions/end-others", both)),
            ("step-up action", await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{colleague.Id}/deactivate", both)),
            ("another impersonation", await host.SendAsync(HttpMethod.Post, $"/api/v1/users/{colleague.Id}/impersonation", both, new { reason = "nested" })),
            ("acting for", await host.SendAsync(HttpMethod.Post, "/api/v1/me/acting-for", both, new { principal_id = colleague.Id })),
        };

        foreach (var (name, response) in refusals)
        {
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: {response.StatusCode} {body}");
            Assert.True(body.Contains(ImpersonationRules.ForbiddenCode, StringComparison.Ordinal), $"{name}: {body}");
            response.Dispose();
        }

        Assert.Equal(PrincipalStatus.Active, (await host.PrincipalAsync(colleague.Id)).Status);
    }

    [Fact]
    [Proves("TF-025 AC2", "TF-025 AC3", "ADR-0049")]
    public async Task TheOperatorSignsInOnceThroughTheirLinkIsNamedOnEveryActionAndIsSignedOutAtTheEnd()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var person = await host.PersonAsync(displayName: "Pat Person");
        var operatorId = Ids.New();
        var handover = new CapturingHandover();
        var issued = await RunAsync(host, handover, services => new OperatorImpersonationCommand(
            new OperatorImpersonationOptions { LoginId = person.LoginId, OperatorId = operatorId, Operator = "Olga Operator", Reason = "Support ticket 7", Minutes = 30 },
            services.GetRequiredService<ImpersonationService>(),
            handover,
            NullLogger<OperatorImpersonationCommand>.Instance));
        var code = handover.Item!.LinkPath.Split('#')[1];

        using var signedIn = await host.SendAsync(HttpMethod.Post, "/auth/sign-in-link", body: new { code });
        using var twice = await host.SendAsync(HttpMethod.Post, "/auth/sign-in-link", body: new { code });
        var cookie = UnitHost.Cookie(signedIn, ImpersonationCookie)!;
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", cookie);
        var meBody = await UnitHost.JsonAsync(me);
        var home = meBody.GetProperty("home_unit").GetProperty("id").GetGuid();
        using var change = await host.SendAsync(HttpMethod.Post, "/api/v1/me/working-unit", cookie, new { unit_id = home });
        var chosen = (await host.AuditAsync(IdentityAuditEvents.WorkingUnitChosen)).Single(e => e.ActorId == person.Id);
        host.Time.Advance(TimeSpan.FromMinutes(31));
        using var after = await host.SendAsync(HttpMethod.Get, "/api/v1/me", cookie);
        var issuedEvent = Assert.Single(await host.AuditAsync(IdentityAuditEvents.ImpersonationLinkIssued, person.LoginId));

        Assert.Equal(0, issued);
        Assert.Equal((OperatorHandoverItem.Impersonation, person.LoginId), (handover.Item.Kind, handover.Item.Subject));
        Assert.StartsWith("/sign-in-link#", handover.Item.LinkPath, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Null(UnitHost.Cookie(signedIn, UnitHost.SessionCookie));
        Assert.Equal(IdentityErrors.SignInLinkInvalid, await UnitHost.ProblemAsync(twice, HttpStatusCode.NotFound));
        Assert.Equal(("Olga Operator", true), (meBody.GetProperty("impersonation").GetProperty("impersonator_name").GetString(), meBody.GetProperty("impersonation").GetProperty("operator").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal(operatorId, chosen.ImpersonatorId);
        Assert.Equal("Olga Operator", JsonDocument.Parse(chosen.Details!).RootElement.GetProperty("impersonator").GetString());
        Assert.Equal(PermissionErrors.ImpersonationEnded, await UnitHost.ProblemAsync(after, HttpStatusCode.Unauthorized));
        Assert.Equal(("Support ticket 7", "high"), (issuedEvent.Reason, issuedEvent.Severity));
        Assert.Contains(await host.SecurityEventsAsync(person.Id), e => e.Kind == SecurityEventKinds.ImpersonationStarted && e.Actor == SecurityEventActors.Operator && e.ActorName == "Olga Operator");
    }

    [Fact]
    [Proves("ADR-0049")]
    public async Task ATenantThatRefusesTheOperatorGetsNoLink()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        await host.SetAsync(IdentitySettings.ImpersonationOperatorAllowed.Key, false);
        var person = await host.PersonAsync();
        var handover = new CapturingHandover();

        var exit = await RunAsync(host, handover, services => new OperatorImpersonationCommand(
            new OperatorImpersonationOptions { LoginId = person.LoginId, OperatorId = Ids.New(), Operator = "Olga Operator", Reason = "Support" },
            services.GetRequiredService<ImpersonationService>(),
            handover,
            NullLogger<OperatorImpersonationCommand>.Instance));

        Assert.Equal(1, exit);
        Assert.Null(handover.Item);
        Assert.Empty(await host.AuditAsync(IdentityAuditEvents.ImpersonationLinkIssued));
    }

    [Fact]
    [Proves("TF-026 AC1", "TF-026 AC3", "ADR-0049")]
    public async Task ABreakGlassAccountSignsInOnceActsWithItsRoleAndEndsWithAReviewOfEveryAction()
    {
        // A tenant with the default MFA policy: the account still needs no enrolment, its operators proved who it is.
        var database = await fixture.CreateTenantDatabaseAsync($"glass_{Ids.New():N}", mfa: true);
        await using var host = await UnitHost.StartAsync(fixture, connectionString: database);
        var accessId = Ids.New();
        var opened = new CapturingHandover();
        var until = host.Time.GetUtcNow().AddHours(1);
        var exit = await RunAsync(host, opened, services => new BreakGlassOpenCommand(
            new BreakGlassOptions { Id = accessId, Operator = "Olga Operator", Approver = "Otto Operator", Ticket = "INC-4711", Reason = "The tenant's administrators are locked out", Until = until },
            services.GetRequiredService<BreakGlassAccounts>(),
            opened,
            NullLogger<BreakGlassOpenCommand>.Instance));
        using var signedIn = await host.SendAsync(HttpMethod.Post, "/auth/sign-in-link", body: new { code = opened.Item!.LinkPath.Split('#')[1] });
        var session = UnitHost.Cookie(signedIn, UnitHost.SessionCookie)!;
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        var meBody = await UnitHost.JsonAsync(me);
        var root = meBody.GetProperty("home_unit").GetProperty("id").GetGuid();
        using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/users", session, new { login_id = $"rescued-{Ids.New():N}@acme.test", display_name = "A new administrator", home_unit_id = root });
        var openEvent = Assert.Single(await host.AuditAsync(IdentityAuditEvents.BreakGlassOpened));
        var closed = new CapturingHandover();
        var closeExit = await RunAsync(host, closed, services => new BreakGlassCloseCommand(
            new BreakGlassOptions { Account = opened.Item.Subject, Reason = "Ended by Olga Operator" },
            services.GetRequiredService<BreakGlassAccounts>(),
            closed,
            services.GetRequiredService<TimeProvider>(),
            NullLogger<BreakGlassCloseCommand>.Instance));
        using var afterClose = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);
        var review = JsonDocument.Parse(closed.Item!.Payload!).RootElement.EnumerateArray().Select(a => a.GetProperty("event_type").GetString()).ToList();
        var permissions = meBody.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        var details = JsonDocument.Parse(openEvent.Details!).RootElement;

        Assert.Equal((0, 0), (exit, closeExit));
        Assert.Equal(OperatorHandoverItem.BreakGlass, opened.Item.Kind);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.False(meBody.GetProperty("mfa_enrolment_required").GetBoolean());
        Assert.Contains(IdentityPermissions.UsersManage.Name, permissions);
        Assert.DoesNotContain(IdentityPermissions.ImpersonationStart.Name, permissions);
        Assert.DoesNotContain(IdentityPermissions.SsoManage.Name, permissions);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(("Olga Operator", "Otto Operator", "INC-4711"), (details.GetProperty("operator").GetString(), details.GetProperty("approver").GetString(), details.GetProperty("ticket").GetString()));
        Assert.Equal(("The tenant's administrators are locked out", AuditSeverity.High), (openEvent.Reason, openEvent.Severity));
        Assert.Equal(HttpStatusCode.Unauthorized, afterClose.StatusCode);
        Assert.Equal(OperatorHandoverItem.BreakGlassReview, closed.Item.Kind);
        Assert.Contains(IdentityAuditEvents.LoginSucceeded, review);
        Assert.Contains(IdentityAuditEvents.UserCreated, review);
        Assert.Equal(review.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonDocument.Parse(Assert.Single(await host.AuditAsync(IdentityAuditEvents.BreakGlassClosed)).Details!).RootElement.GetProperty("actions").GetString());
    }

    [Fact]
    [Proves("TF-026 AC4", "ADR-0049")]
    public async Task ABreakGlassAccountNeedsTwoOperatorsAndStopsAtItsTimeWhateverTheFleetDoes()
    {
        await using var host = await UnitHost.StartAsync(fixture);
        var same = await host.WithScopeAsync(services => services.GetRequiredService<BreakGlassAccounts>().OpenAsync(
            new BreakGlassOptions { Id = Ids.New(), Operator = "Olga Operator", Approver = "olga operator", Ticket = "INC-1", Reason = "Test", Until = host.Time.GetUtcNow().AddHours(1) },
            TestContext.Current.CancellationToken));
        var opened = await host.WithScopeAsync(services => services.GetRequiredService<BreakGlassAccounts>().OpenAsync(
            new BreakGlassOptions { Id = Ids.New(), Operator = "Olga Operator", Approver = "Otto Operator", Ticket = "INC-2", Reason = "Test", Until = host.Time.GetUtcNow().AddMinutes(30) },
            TestContext.Current.CancellationToken));
        using var signedIn = await host.SendAsync(HttpMethod.Post, "/auth/sign-in-link", body: new { code = opened.Value.Token });
        var session = UnitHost.Cookie(signedIn, UnitHost.SessionCookie)!;
        host.Time.Advance(TimeSpan.FromMinutes(31));
        using var late = await host.SendAsync(HttpMethod.Get, "/api/v1/me", session);

        Assert.Equal(IdentityErrors.BreakGlassSameOperator, same.Error.Code);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);
    }

    /// <summary>Runs a unit command in a scope of the host, as the image runs it, with the handover the test captures.</summary>
    private static Task<int> RunAsync(UnitHost host, CapturingHandover handover, Func<IServiceProvider, IUnitCommand> command)
    {
        Assert.NotNull(handover);
        return host.WithScopeAsync(services => command(services).RunAsync([], TestContext.Current.CancellationToken));
    }

    /// <summary>The store's one-time place, as the command sees it: what it deposited.</summary>
    private sealed class CapturingHandover : IOperatorHandover
    {
        public OperatorHandoverItem? Item { get; private set; }

        public bool IsAvailable => true;

        public Task DepositAsync(OperatorHandoverItem item, CancellationToken cancellationToken)
        {
            Item = item;
            return Task.CompletedTask;
        }
    }
}
