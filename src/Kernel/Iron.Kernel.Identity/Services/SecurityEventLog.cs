using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Writes a person's security event (TF-019, ADR-0046) and its audit event, inside the caller's transaction: the row My
/// profile lists and the line the auditors read are one change (ADR-0013). The actor is whoever the request runs as,
/// unless <c>actorId</c> says otherwise (a sign-in that locked a login is anonymous); <paramref name="actor"/> says which kind
/// of actor it was, and <paramref name="actorName"/> names an administrator or the operator.
/// </summary>
public sealed class SecurityEventLog(IdentityDbContext context, IAuditWriter audit, ICurrentPrincipal current, TimeProvider time)
{
    public async Task RecordAsync(Principal principal, string kind, string actor, string? actorName, string? reason, IReadOnlyDictionary<string, string>? details, CancellationToken cancellationToken, Guid? actorId = null, string? severity = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        context.SecurityEvents.Add(new SecurityEvent(principal.Id, kind, time.GetUtcNow(), actor, actorName, reason));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(
            new AuditEvent(SecurityEventKinds.AuditEventOf(kind), AuditOutcome.Success, actorId ?? current.Id, principal.LoginId, details, UserService.RecordType, principal.Id, reason, Severity: severity),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Another module's change to a person's access in the person's security activity (ADR-0057): the row only, since the
/// module audits its own change. The actor is the request's principal, named when it is someone else: an administrator.
/// </summary>
public sealed class SecurityActivity(IdentityDbContext context, ICurrentPrincipal current, TimeProvider time) : ISecurityActivity
{
    public async Task NoteAsync(Guid principalId, string kind, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var self = principalId == current.Id;
        var actorName = self ? null : await context.Principals.AsNoTracking().Where(p => p.Id == current.Id).Select(p => p.DisplayName).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        context.SecurityEvents.Add(new SecurityEvent(principalId, kind, time.GetUtcNow(), self ? SecurityEventActors.Self : SecurityEventActors.Administrator, actorName, null));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
