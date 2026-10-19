using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>A user just created: the principal and the invitation token for the link, which is never stored.</summary>
public sealed record NewUser(Principal Principal, string InvitationToken, DateTimeOffset InvitationExpiresAt);

/// <summary>
/// Users created by an administrator (A2): a login id, a display name and a home unit, in Invited status with an open
/// invitation. The endpoint checks <c>identity.users.manage</c> at the home unit first; email delivery is S4, so the
/// caller passes the link on. The principal, its invitation and the audit events are one transaction.
/// </summary>
public sealed class UserService(
    IdentityDbContext context,
    InvitationService invitations,
    IOrganisationUnits units,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor)
{
    public const string RecordType = "principal";

    public async Task<Result<NewUser>> CreateAsync(string loginId, string displayName, Guid homeUnitId, CancellationToken cancellationToken)
    {
        var normalised = Principal.NormaliseLoginId(loginId);
        var unit = await units.FindAsync(homeUnitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            return Error.Validation(IdentityErrors.HomeUnitNotFound, "There is no organisation unit with this id.", new FieldError("home_unit_id", "not_found", "Choose an existing unit."));
        }

        if (unit.Status != UnitStatus.Active)
        {
            return Error.Validation(IdentityErrors.HomeUnitInactive, $"The unit '{unit.Name}' is deactivated; nobody can be created in it.", new FieldError("home_unit_id", "inactive", "Choose an active unit."));
        }

        if (await context.Principals.AnyAsync(p => p.LoginId == normalised, cancellationToken).ConfigureAwait(false))
        {
            return new Error(ErrorKind.Conflict, IdentityErrors.LoginIdTaken, $"Someone already signs in as '{normalised}'.", [new FieldError("login_id", "taken", "Another account has this login id. Choose another.")]);
        }

        var principal = new Principal(normalised, PrincipalType.User, displayName, normalised.Contains('@', StringComparison.Ordinal) ? normalised : null);
        principal.SetHomeUnit(unit.Id);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.Principals.Add(principal);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var token = await invitations.InviteAsync(principal, cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["display_name"] = principal.DisplayName, ["home_unit_id"] = unit.Id.ToString() };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.UserCreated, AuditOutcome.Success, actor.Id, principal.LoginId, details, RecordType, principal.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new NewUser(principal, token, principal.InvitationExpiresAt!.Value);
    }
}
