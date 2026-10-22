using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Handover;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>Bound from the <c>Recovery</c> section: what the fleet hands the <c>recover-admin</c> command (ADR-0046).</summary>
public sealed class AdministratorRecoveryOptions
{
    public const string SectionName = "Recovery";

    /// <summary>The tenant administrator who needs a new password.</summary>
    public string? LoginId { get; set; }

    /// <summary>Who asked, as the control plane knows them: the tenant's audit trail names them.</summary>
    public string? Operator { get; set; }

    /// <summary>Why, in the operator's words: required.</summary>
    public string? Reason { get; set; }

    /// <summary>Also remove the administrator's second factors, for one who lost their device too (TF-013 AC3, ADR-0047).</summary>
    public bool ResetFactors { get; set; }
}

/// <summary>
/// The operator's recovery link for a tenant administrator (TF-017, ADR-0046): the control plane cannot reach a unit's
/// database, so the fleet runs this command in the unit with the request. It issues the link only for an active person
/// who holds the tenant administrator role for the whole tenant, records it as their security event and in the tenant's
/// audit trail with the operator and the reason, and hands the link over through the one-time place the fleet gave it
/// (ADR-0046): the control plane gives it once to the operator who asked. The link is never in a log line or the
/// command's output. When asked (<c>Recovery:ResetFactors</c>), it also removes the administrator's second factors and
/// ends their sessions, recorded the same way (ADR-0047). Exit code 0 when the link was handed over; 1 when the request
/// is refused, with the reason logged.
/// </summary>
public sealed class AdministratorRecoveryCommand(
    AdministratorRecoveryOptions options,
    IdentityDbContext context,
    PasswordService passwords,
    FactorService factors,
    SessionService sessions,
    ITenantAdministrators administrators,
    SsoPeople ssoPeople,
    IUnitOfWork unitOfWork,
    IOperatorHandover handover,
    ILogger<AdministratorRecoveryCommand> logger) : IUnitCommand
{
    public string Name => DatabaseCommands.RecoverAdministrator;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!handover.IsAvailable)
        {
            logger.LogError("No recovery link was issued: the command has no handover place (Handover:Path and Handover:Token), which only the fleet gives.");
            return 1;
        }

        var result = await IssueAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            logger.LogError("No recovery link was issued ({Code}): {Message}", result.Error.Code, result.Error.Message);
            return 1;
        }

        var loginId = Principal.NormaliseLoginId(options.LoginId);
        await handover.DepositAsync(new OperatorHandoverItem(OperatorHandoverItem.Recovery, loginId, Links.Recovery(result.Value.Token), result.Value.ExpiresAt), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Handed over a recovery link for {LoginId} to the operator who asked; it works once, until {ExpiresAt}.", loginId, result.Value.ExpiresAt);
        return 0;
    }

    public async Task<Result<RecoveryLink>> IssueAsync(CancellationToken cancellationToken)
    {
        var loginId = Principal.NormaliseLoginId(options.LoginId);
        var operatorName = options.Operator?.Trim();
        var reason = options.Reason?.Trim();
        if (loginId.Length == 0 || string.IsNullOrEmpty(operatorName) || string.IsNullOrEmpty(reason))
        {
            return Error.Validation(IdentityErrors.RecoveryNotAllowed, "The operator's recovery needs Recovery:LoginId, Recovery:Operator and Recovery:Reason.");
        }

        var principal = await context.Principals.SingleOrDefaultAsync(p => p.LoginId == loginId, cancellationToken).ConfigureAwait(false);
        if (principal is null || !await administrators.IsTenantAdministratorAsync(principal.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Forbidden(IdentityErrors.RecoveryNotAdministrator, $"'{loginId}' is not a tenant administrator of this tenant; the operator recovers only those.");
        }

        if (principal.Type != PrincipalType.User || principal.Status != PrincipalStatus.Active || principal.PasswordHash is null)
        {
            return Error.Conflict(IdentityErrors.RecoveryNotAllowed, $"'{loginId}' is {principal.Status.ToString().ToLowerInvariant()}: a recovery link sets the password of an active person. An invited administrator needs the seed's new invitation (make update-unit).");
        }

        if ((await ssoPeople.SsoOnlyAsync(principal, cancellationToken).ConfigureAwait(false)).SsoOnly)
        {
            return Error.Conflict(IdentityErrors.RecoverySsoOnly, $"'{loginId}' signs in only through single sign-on, where a recovery link does not apply (ADR-0048). Recover another of the tenant's administrators, one who signs in with a password.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["operator"] = operatorName, ["source"] = "control_plane" };
        var token = await passwords.OpenRecoveryAsync(principal, SecurityEventActors.Operator, operatorName, reason, details, cancellationToken).ConfigureAwait(false);
        if (options.ResetFactors)
        {
            // The link sets the password; with the factors gone the administrator signs in with it and enrols again at once.
            var ended = await sessions.EndAllAsync(principal.Id, SessionEndReasons.Revoked, cancellationToken).ConfigureAwait(false);
            var resetDetails = new Dictionary<string, string>(details, StringComparer.Ordinal) { ["sessions_ended"] = ended.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var removed = await factors.ResetAsync(principal, SecurityEventActors.Operator, operatorName, reason, resetDetails, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("The operator's recovery of {LoginId} also reset {Removed} second factor(s).", loginId, removed);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RecoveryLink(token, principal.RecoveryExpiresAt!.Value);
    }
}
