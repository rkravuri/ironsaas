using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Identity.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// Ends the delegations of a person Identity deactivated (TF-037, ADR-0060): every running or scheduled delegation the
/// person gives or holds, as the system's, each audited and announced as ended, under the lock every change of delegations
/// takes. Nobody acts for a deactivated person, nor does a deactivated person act for anyone. Its subscription reads
/// <see cref="IdentityEvents.PersonDeactivated"/>; a repeated delivery finds nothing running and changes nothing.
/// </summary>
public sealed class DelegationsOfDeactivated(AuthorizationDbContext context, IAuditWriter audit, IEventPublisher events, ConflictService segregation, TimeProvider time) : IEventHandler<PersonDeactivated>
{
    public const string Subscription = "authz.end_delegations_of_deactivated";

    public const string Reason = "The person was deactivated.";

    public async Task<Result> HandleAsync(DomainEvent<PersonDeactivated> domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var person = domainEvent.Data.PersonId;
        var now = time.GetUtcNow();
        await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({DelegationService.DelegationsLockKey})", cancellationToken).ConfigureAwait(false);
        var running = await context.Delegations
            .Where(d => (d.DelegatorId == person || d.DelegateId == person) && d.EndedAt == null && d.EndsAt > now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (running.Count == 0)
        {
            return Result.Success();
        }

        foreach (var delegation in running)
        {
            delegation.End(now, SystemPrincipal.SystemId, Reason);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var delegation in running)
        {
            await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.DelegationEnded, AuditOutcome.Success, SystemPrincipal.SystemId, null, DelegationService.Details(delegation), DelegationService.RecordType, delegation.Id, Reason), cancellationToken).ConfigureAwait(false);
            await events.PublishAsync(AuthorizationEvents.DelegationEnded, delegation.Id, new DelegationEnded(delegation.Id, delegation.DelegatorId, delegation.DelegateId, DelegationEnded.PersonDeactivated), cancellationToken).ConfigureAwait(false);
        }

        foreach (var delegateId in running.Select(d => d.DelegateId).Distinct())
        {
            await segregation.RefreshPrincipalAsync(delegateId, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }
}
