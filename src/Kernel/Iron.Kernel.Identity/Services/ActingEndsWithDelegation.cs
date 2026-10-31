using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// When a delegation ends, the delegate's live sessions that act for the delegator stop acting at once, each audited, as
/// the delegate's next request would otherwise find (ADR-0037, ADR-0060). A delegation that still runs from the same
/// delegator to the same delegate, for other duties, keeps them acting. Its subscription reads
/// <see cref="AuthorizationEvents.DelegationEnded"/>; a repeated delivery finds no session acting and changes nothing.
/// </summary>
public sealed class ActingEndsWithDelegation(IdentityDbContext context, IDelegations delegations, IAuditWriter audit) : IEventHandler<DelegationEnded>
{
    public const string Subscription = "identity.stop_acting_for_ended_delegation";

    public async Task<Result> HandleAsync(DomainEvent<DelegationEnded> domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var ended = domainEvent.Data;
        if (await delegations.ActiveAsync(ended.DelegatorId, ended.DelegateId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Result.Success();
        }

        var acting = await context.Sessions
            .Where(s => s.PrincipalId == ended.DelegateId && s.ActingForId == ended.DelegatorId && s.EndedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (acting.Count == 0)
        {
            return Result.Success();
        }

        foreach (var session in acting)
        {
            session.StopActing();
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var loginId = await context.Principals.Where(p => p.Id == ended.DelegateId).Select(p => p.LoginId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in acting)
        {
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.ActingEnded, AuditOutcome.Success, ended.DelegateId, loginId, RecordType: "session", RecordId: session.Id, OnBehalfOfId: ended.DelegatorId), cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }
}
