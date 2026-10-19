using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Invitations (TF-018): a signed-in administrator or the seeder opens one; the invitee sets a password and becomes
/// Active. The link is valid for the days the invitee's home unit says (<c>identity.invitation.valid_days</c>, walking up
/// to the tenant, ADR-0036).
/// </summary>
public sealed class InvitationService(
    IdentityDbContext context,
    IPasswordHasher hasher,
    IdentityOptions options,
    ISettings settings,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    IAuditWriter audit,
    ILogger<InvitationService> logger)
{
    /// <summary>Opens or replaces the invitation of a principal and returns the token for the link. The token is never stored.</summary>
    public async Task<string> InviteAsync(Principal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var token = SecureTokens.New();
        var where = principal.HomeUnitId is { } unit ? SettingContext.ForUnit(unit) : SettingContext.Tenant;
        var days = await settings.GetAsync(IdentitySettings.InvitationValidDays, where, cancellationToken).ConfigureAwait(false);
        principal.Invite(SecureTokens.Hash(token), time.GetUtcNow() + TimeSpan.FromDays(days));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.InvitationCreated, AuditOutcome.Success, principal.Id, principal.LoginId, RecordType: "principal", RecordId: principal.Id), cancellationToken).ConfigureAwait(false);
        return token;
    }

    public async Task<Result<Principal>> AcceptAsync(string token, string password, string? displayName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Error.NotFound(IdentityErrors.InvitationNotFound, "This invitation does not exist.");
        }

        var hash = SecureTokens.Hash(token);
        var principal = await context.Principals.SingleOrDefaultAsync(p => p.InvitationTokenHash == hash, cancellationToken).ConfigureAwait(false);
        if (principal is null)
        {
            return Error.NotFound(IdentityErrors.InvitationNotFound, "This invitation does not exist.");
        }

        var now = time.GetUtcNow();
        if (!principal.HasOpenInvitation(now))
        {
            return Error.Validation(IdentityErrors.InvitationExpired, "This invitation has expired. Ask for a new one.");
        }

        var policy = CheckPassword(password, principal.LoginId);
        if (policy.IsFailure)
        {
            return policy.Error;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        principal.AcceptInvitation(hasher.Hash(password), displayName);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.InvitationAccepted, AuditOutcome.Success, principal.Id, principal.LoginId, RecordType: "principal", RecordId: principal.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Principal {PrincipalId} accepted its invitation.", principal.Id);
        return principal;
    }

    /// <summary>The S1 password rules (TF-012: length 12; the common-password list and history arrive in S3).</summary>
    public Result CheckPassword(string password, string loginId)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < options.Password.MinimumLength || password.Length > options.Password.MaximumLength)
        {
            return Error.Validation(IdentityErrors.PasswordTooShort, $"The password needs at least {options.Password.MinimumLength} characters.", new FieldError("password", "too_short", $"At least {options.Password.MinimumLength} characters."));
        }

        if (string.Equals(password.Trim(), loginId, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(IdentityErrors.PasswordIsLoginId, "The password must differ from the login identifier.", new FieldError("password", "is_login_id", "Choose a different password."));
        }

        return Result.Success();
    }
}
