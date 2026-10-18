using Iron.Api.Permissions;
using Iron.Authorization.Contracts;
using Iron.Authorization.Data;
using Iron.Authorization.Services;
using Iron.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Authorization.Tests;

[Collection(AuthorizationCollection.Name)]
public class PermissionEvaluatorTests(AuthorizationFixture fixture)
{
    private static AccessRequest ReadRoles(Guid principal) => new(principal, AuthorizationPermissions.RolesRead, "GET /api/v1/roles", "/api/v1/roles");

    [Fact]
    public async Task APrincipalWithoutARoleIsDeniedAndTheDenialIsAudited()
    {
        var audit = new RecordingAuditWriter();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), audit);
        await using var scope = provider.CreateAsyncScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>();
        var nobody = Ids.New();

        var decision = await evaluator.EvaluateAsync(ReadRoles(nobody), TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
        Assert.Equal(AccessDecision.NotGranted, decision.Reason);
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal(PermissionEvaluator.DenialAction, auditEvent.Action);
        Assert.Equal(Iron.Audit.Contracts.AuditOutcome.Denied, auditEvent.Outcome);
        Assert.Equal(nobody, auditEvent.PrincipalId);
        Assert.Equal("GET /api/v1/roles", auditEvent.Target);
        Assert.Equal(AuthorizationPermissions.RolesRead.Name, auditEvent.Details?["permission"]);
    }

    [Fact]
    public async Task TheTenantAdministratorRoleGrantsEveryPermissionAndAssigningTwiceIsHarmless()
    {
        var audit = new RecordingAuditWriter();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), audit);
        await using var scope = provider.CreateAsyncScope();
        var assignments = scope.ServiceProvider.GetRequiredService<IRoleAssignments>();
        var evaluator = scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>();
        var alice = Ids.New();

        var first = await assignments.AssignAsync(alice, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken);
        var second = await assignments.AssignAsync(alice, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal([SystemRoles.TenantAdministrator], await assignments.RoleNamesOfAsync(alice, TestContext.Current.CancellationToken));
        foreach (var permission in AuthorizationPermissions.All)
        {
            var decision = await evaluator.EvaluateAsync(new AccessRequest(alice, permission, "GET /x", "/x"), TestContext.Current.CancellationToken);
            Assert.True(decision.Allowed, permission.Name);
        }

        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task AnUnknownRoleCannotBeAssigned()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter());
        await using var scope = provider.CreateAsyncScope();
        var assignments = scope.ServiceProvider.GetRequiredService<IRoleAssignments>();

        var result = await assignments.AssignAsync(Ids.New(), "no-such-role", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthorizationErrors.RoleNotFound, result.Error.Code);
    }

    [Fact]
    public async Task AGrantMadeBehindTheCacheShowsWithinSixtySeconds()
    {
        var time = new FakeTimeProvider();
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter());
        var bob = Ids.New();

        await using (var scope = provider.CreateAsyncScope())
        {
            var evaluator = scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>();
            Assert.False((await evaluator.EvaluateAsync(ReadRoles(bob), TestContext.Current.CancellationToken)).Allowed);

            // A grant written directly to the table, as another unit process or an operator would.
            var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            var readOnly = await context.Roles.SingleAsync(r => r.Name == SystemRoles.ReadOnly, TestContext.Current.CancellationToken);
            context.RoleAssignments.Add(new RoleAssignment(readOnly.Id, bob));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var evaluator = scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>();
            Assert.False((await evaluator.EvaluateAsync(ReadRoles(bob), TestContext.Current.CancellationToken)).Allowed, "still cached");

            time.Advance(TimeSpan.FromSeconds(61));
            Assert.True((await evaluator.EvaluateAsync(ReadRoles(bob), TestContext.Current.CancellationToken)).Allowed, "cache expired");
        }
    }

    [Fact]
    public async Task WhenTheDatabaseIsUnavailableAccessIsDenied()
    {
        var audit = new RecordingAuditWriter();
        await using var provider = fixture.BuildServices(
            new FakeTimeProvider(),
            new FixedPrincipal(SystemPrincipal.SystemId),
            audit,
            connectionString: "Host=localhost;Port=1;Database=nowhere;Username=nobody;Password=nothing;Timeout=1");
        await using var scope = provider.CreateAsyncScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>();

        var decision = await evaluator.EvaluateAsync(ReadRoles(Ids.New()), TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
        Assert.Equal(AccessDecision.Unavailable, decision.Reason);
        Assert.Equal(AccessDecision.Unavailable, Assert.Single(audit.Events).Details?["reason"]);
    }
}
