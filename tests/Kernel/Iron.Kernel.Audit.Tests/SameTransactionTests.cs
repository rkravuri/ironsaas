using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Audit.Tests;

/// <summary>TF-060 AC1: the audit event and the change it describes are one transaction, across two module contexts.</summary>
[Collection(AuditCollection.Name)]
public class SameTransactionTests(AuditFixture fixture)
{
    [Fact]
    [Proves("TF-060 AC1", "TF-084 AC3", "ADR-0013")]
    public async Task WithoutCommitNeitherTheChangeNorTheEventExists()
    {
        var alice = Ids.New();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));

        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using (await unitOfWork.BeginAsync(TestContext.Current.CancellationToken))
            {
                await Grant(scope.ServiceProvider, alice);
            }
        }

        await AssertStored(provider, alice, expected: false);
    }

    [Fact]
    [Proves("TF-060 AC1", "ADR-0013")]
    public async Task WithCommitBothTheChangeAndTheEventExist()
    {
        var alice = Ids.New();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));

        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginAsync(TestContext.Current.CancellationToken);
            await Grant(scope.ServiceProvider, alice);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        await AssertStored(provider, alice, expected: true);
    }

    private static async Task Grant(IServiceProvider services, Guid principal)
    {
        var assignments = services.GetRequiredService<IRoleAssignments>();
        Assert.True((await assignments.AssignAsync(principal, SystemRoles.ReadOnly, TestContext.Current.CancellationToken)).IsSuccess);
        var audit = services.GetRequiredService<IAuditWriter>();
        await audit.WriteAsync(new AuditEvent("tests.role.assigned", AuditOutcome.Success, principal, SystemRoles.ReadOnly), TestContext.Current.CancellationToken);
    }

    private static async Task AssertStored(ServiceProvider provider, Guid principal, bool expected)
    {
        await using var scope = provider.CreateAsyncScope();
        var roles = await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().RoleCodesOfAsync(principal, TestContext.Current.CancellationToken);
        var events = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var eventExists = await events.Events.AsNoTracking().AnyAsync(e => e.ActorId == principal, TestContext.Current.CancellationToken);

        Assert.Equal(expected, roles.Count > 0);
        Assert.Equal(expected, eventExists);
    }
}
