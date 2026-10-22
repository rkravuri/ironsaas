using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>What a person offers as their second factor: a code from their app, a recovery code, or a passkey's answer.</summary>
public sealed record SecondFactorProof(string? Code, string? RecoveryCode, JsonElement? Passkey)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Code) && string.IsNullOrWhiteSpace(RecoveryCode) && Passkey is not { ValueKind: JsonValueKind.Object };
}

/// <summary>An authenticator app waiting for its first code: the secret as text and as the URI the QR code shows.</summary>
public sealed record AuthenticatorAppSetup(string Secret, string Uri, DateTimeOffset ExpiresAt);

/// <summary>A factor just enrolled, and the recovery codes when it is the person's first: shown once.</summary>
public sealed record Enrolled(Factor Factor, IReadOnlyList<string>? RecoveryCodes);

/// <summary>A person's second factors as My profile shows them, and what the policy asks of them.</summary>
public sealed record FactorsOverview(IReadOnlyList<Factor> Factors, int RecoveryCodesLeft, bool Required, IReadOnlyList<string> Allowed, DateTimeOffset? EnrolBy);

/// <summary>
/// Second factors (TF-013, ADR-0047): enrolling an authenticator app or a passkey, proving one at sign-in or at a
/// re-authentication, recovery codes, removal, and a reset by an administrator or the operator. A factor is saved only
/// after it proved itself: an authenticator app with its first code, a passkey with its registration. The first factor
/// brings ten recovery codes, shown once. Wrong codes count against the person: every third in a row starts a cooling
/// period and the tenth locks the login as wrong passwords do. Every enrolment, removal, reset and recovery-code use is a
/// security event of the person's and an audit event, in the same transaction as the change.
/// </summary>
public sealed class FactorService(
    IdentityDbContext context,
    FactorProtector protector,
    Passkeys passkeys,
    RecoveryCodes recoveryCodes,
    MfaPolicy policy,
    LoginThrottles throttles,
    SecurityEventLog securityEvents,
    AuthenticationChallenges challenges,
    TenantContext tenant,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    TimeProvider time,
    ILogger<FactorService> logger)
{
    /// <summary>The name of a sign-in by recovery code, beside the factor kinds.</summary>
    public const string RecoveryCodeMethod = "recovery_code";

    public const string InvalidMessage = "That did not prove it is you. Check the code, or try another way.";

    private const int MaxAttempts = 3;

    public async Task<IReadOnlyList<Factor>> OfAsync(Guid principalId, CancellationToken cancellationToken) =>
        await context.Factors.AsNoTracking().Where(f => f.PrincipalId == principalId).OrderBy(f => f.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<bool> HasAnyAsync(Guid principalId, CancellationToken cancellationToken) =>
        context.Factors.AnyAsync(f => f.PrincipalId == principalId, cancellationToken);

    /// <summary>The ways the person can prove their second factor now: their factors' kinds, and recovery codes while any is left.</summary>
    public async Task<IReadOnlyList<string>> MethodsAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var kinds = await context.Factors.AsNoTracking().Where(f => f.PrincipalId == principalId).Select(f => f.Kind).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var methods = FactorKinds.All.Where(kind => kinds.Contains(kind)).ToList();
        if (await recoveryCodes.RemainingAsync(principalId, cancellationToken).ConfigureAwait(false) > 0)
        {
            methods.Add(RecoveryCodeMethod);
        }

        return methods;
    }

    public async Task<FactorsOverview> OverviewAsync(Principal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var rules = await policy.RulesAsync(cancellationToken).ConfigureAwait(false);
        var factors = await OfAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        var required = await policy.IsRequiredAsync(principal, rules, cancellationToken).ConfigureAwait(false);
        var grace = MfaPolicy.GraceDaysFor(principal, rules);
        var enrolBy = required && factors.Count == 0 && principal.MfaRequiredSince is { } since && grace > 0 ? since.AddDays(grace) : (DateTimeOffset?)null;
        return new FactorsOverview(factors, await recoveryCodes.RemainingAsync(principal.Id, cancellationToken).ConfigureAwait(false), required, rules.Allowed, enrolBy);
    }

    /// <summary>
    /// The options for the person to sign with one of their passkeys, kept with the challenge; null when they have none.
    /// </summary>
    public async Task<string?> PasskeyAssertionAsync(AuthenticationChallenge challenge, Guid principalId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var keys = await context.Factors.AsNoTracking().Where(f => f.PrincipalId == principalId && f.Kind == FactorKinds.Passkey).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            return null;
        }

        var options = passkeys.AssertionOptions(keys);
        challenge.Expect(options);
        await challenges.SaveAsync(cancellationToken).ConfigureAwait(false);
        return options;
    }

    /// <summary>
    /// Checks the person's second factor (TF-013 AC2). During a cooling period nothing is checked. A wrong answer counts:
    /// every third in a row starts a cooling period of 60 seconds that doubles with each further three, up to 15 minutes,
    /// and the tenth locks the login for the tenant's lockout duration. The answer never says which part was wrong. A right
    /// one clears the count and answers the method that proved it. Its own transaction: a failure is counted even though
    /// the caller goes no further.
    /// </summary>
    public async Task<Result<string>> VerifyAsync(Principal principal, SecondFactorProof proof, AuthenticationChallenge? challenge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(proof);
        var now = time.GetUtcNow();
        if (principal.IsCooling(now))
        {
            return Cooling(principal, now);
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var proved = await CheckAsync(principal, proof, challenge, now, cancellationToken).ConfigureAwait(false);
        if (proved is null)
        {
            var failure = await FailAsync(principal, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return failure;
        }

        principal.RecordSecondFactorSuccess();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (proved == RecoveryCodeMethod)
        {
            var left = await recoveryCodes.RemainingAsync(principal.Id, cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["recovery_codes_left"] = left.ToString(CultureInfo.InvariantCulture) };
            await securityEvents.RecordAsync(principal, SecurityEventKinds.RecoveryCodeUsed, SecurityEventActors.Self, null, null, details, cancellationToken, actorId: principal.Id).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return proved;
    }

    /// <summary>Starts an authenticator app for the challenge: a new secret, kept encrypted with the challenge until its first code.</summary>
    public async Task<Result<AuthenticatorAppSetup>> BeginAuthenticatorAppAsync(AuthenticationChallenge challenge, Principal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(principal);
        var allowed = await AllowedAsync(FactorKinds.AuthenticatorApp, cancellationToken).ConfigureAwait(false);
        if (allowed is not null)
        {
            return allowed;
        }

        if (await context.Factors.AnyAsync(f => f.PrincipalId == principal.Id && f.Kind == FactorKinds.AuthenticatorApp, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(IdentityErrors.FactorAppExists, "You have an authenticator app already. Remove it first to set up another.");
        }

        var secret = Totp.NewSecret();
        try
        {
            challenge.Hold(protector.Seal(secret, principal.Id));
            await challenges.SaveAsync(cancellationToken).ConfigureAwait(false);
            return new AuthenticatorAppSetup(Totp.Base32(secret), Totp.Uri(Issuer(), principal.LoginId, secret), challenge.ExpiresAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>The first code of the app waiting with the challenge: when it matches, the app is saved as a factor.</summary>
    public async Task<Result<Enrolled>> ConfirmAuthenticatorAppAsync(AuthenticationChallenge challenge, Principal principal, string? code, string? name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(principal);
        if (challenge.PendingSecret is not { } pending)
        {
            return Error.Conflict(IdentityErrors.FactorInvalid, "There is no authenticator app waiting for its first code. Start again.");
        }

        var allowed = await AllowedAsync(FactorKinds.AuthenticatorApp, cancellationToken).ConfigureAwait(false);
        if (allowed is not null)
        {
            return allowed;
        }

        var now = time.GetUtcNow();
        var secret = protector.Open(pending, principal.Id);
        long? step;
        try
        {
            step = Totp.Match(secret, code, now, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        if (step is null)
        {
            return Error.Validation(IdentityErrors.FactorInvalid, "The code does not match. Check that the time on your phone is right, and type the code it shows now.", new FieldError("code", "invalid", "Type the six digits your app shows now."));
        }

        var factor = Factor.AuthenticatorApp(principal.Id, string.IsNullOrWhiteSpace(name) ? "Authenticator app" : name, pending, step.Value, now);
        return await SaveAsync(challenge, principal, factor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The options for the person's authenticator to create a passkey, kept with the challenge.</summary>
    public async Task<Result<string>> BeginPasskeyAsync(AuthenticationChallenge challenge, Principal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(principal);
        var allowed = await AllowedAsync(FactorKinds.Passkey, cancellationToken).ConfigureAwait(false);
        if (allowed is not null)
        {
            return allowed;
        }

        var existing = await OfAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        var options = passkeys.RegistrationOptions(principal, existing);
        challenge.Expect(options);
        await challenges.SaveAsync(cancellationToken).ConfigureAwait(false);
        return options;
    }

    /// <summary>The authenticator's new passkey: when it verifies against the challenge's options, it is saved with the name chosen.</summary>
    public async Task<Result<Enrolled>> FinishPasskeyAsync(AuthenticationChallenge challenge, Principal principal, string? name, JsonElement credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(principal);
        var allowed = await AllowedAsync(FactorKinds.Passkey, cancellationToken).ConfigureAwait(false);
        if (allowed is not null)
        {
            return allowed;
        }

        // Only the options of a creation name the algorithms a new key may use.
        if (challenge.WebAuthnOptions is not { } options || !options.Contains("\"pubKeyCredParams\"", StringComparison.Ordinal))
        {
            return Error.Conflict(IdentityErrors.FactorInvalid, "There is no passkey being created. Start again.");
        }

        var created = await passkeys.RegisterAsync(options, credential, cancellationToken).ConfigureAwait(false);
        if (created is null || !created.UserHandle.AsSpan().SequenceEqual(Passkeys.UserHandle(principal.Id)))
        {
            return Error.Validation(IdentityErrors.FactorInvalid, "The passkey was not created. Try again, or use another authenticator.");
        }

        var factor = Factor.Passkey(principal.Id, string.IsNullOrWhiteSpace(name) ? "Passkey" : name, created.CredentialId, created.PublicKey, created.SignCount, created.UserHandle, created.AaGuid);
        return await SaveAsync(challenge, principal, factor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The person names one of their factors anew. Audited; not a security event, since nothing about sign-in changes.</summary>
    public async Task<Result<Factor>> RenameAsync(Principal principal, Guid factorId, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var factor = await context.Factors.SingleOrDefaultAsync(f => f.Id == factorId && f.PrincipalId == principal.Id, cancellationToken).ConfigureAwait(false);
        if (factor is null)
        {
            return Error.NotFound(IdentityErrors.FactorNotFound, "You have no factor with this id.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        factor.Rename(name);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.FactorRenamed, AuditOutcome.Success, principal.Id, principal.LoginId, Details(factor), "factor", factor.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return factor;
    }

    /// <summary>
    /// The person removes one of their factors. The last one stays while the policy requires a factor of them; when the
    /// last one goes, so do the recovery codes, which only stand in for a factor.
    /// </summary>
    public async Task<Result> RemoveAsync(Principal principal, Guid factorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var factor = await context.Factors.SingleOrDefaultAsync(f => f.Id == factorId && f.PrincipalId == principal.Id, cancellationToken).ConfigureAwait(false);
        if (factor is null)
        {
            return Error.NotFound(IdentityErrors.FactorNotFound, "You have no factor with this id.");
        }

        var others = await context.Factors.CountAsync(f => f.PrincipalId == principal.Id && f.Id != factorId, cancellationToken).ConfigureAwait(false);
        if (others == 0 && await policy.IsRequiredAsync(principal, await policy.RulesAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(IdentityErrors.FactorLast, "This is your last second factor, and your organisation requires one. Add another first, then remove this one.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.Factors.Remove(factor);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (others == 0)
        {
            await context.RecoveryCodes.Where(c => c.PrincipalId == principal.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await securityEvents.RecordAsync(principal, SecurityEventKinds.FactorRemoved, SecurityEventActors.Self, null, null, Details(factor), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>A new set of ten recovery codes, shown once; the old ones stop working.</summary>
    public async Task<Result<IReadOnlyList<string>>> RegenerateRecoveryCodesAsync(Principal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!await HasAnyAsync(principal.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(IdentityErrors.FactorNone, "You have no second factor, so recovery codes would not help. Add an authenticator app or a passkey first.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var codes = await recoveryCodes.ReplaceAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        await securityEvents.RecordAsync(principal, SecurityEventKinds.RecoveryCodesRegenerated, SecurityEventActors.Self, null, null, null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(codes);
    }

    /// <summary>
    /// Removes every factor and recovery code of the person (TF-013 AC3): an administrator's reset or the operator's, with
    /// the reason, recorded as the person's security event. The caller holds the transaction, ends the person's sessions
    /// and has checked that the actor may.
    /// </summary>
    public async Task<int> ResetAsync(Principal principal, string actor, string? actorName, string reason, IReadOnlyDictionary<string, string>? details, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var removed = await context.Factors.Where(f => f.PrincipalId == principal.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.RecoveryCodes.Where(c => c.PrincipalId == principal.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await challenges.EndAllOfAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        principal.RecordSecondFactorSuccess();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var recorded = new Dictionary<string, string>(details ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["factors_removed"] = removed.ToString(CultureInfo.InvariantCulture) };
        await securityEvents.RecordAsync(principal, SecurityEventKinds.FactorsReset, actor, actorName, reason, recorded, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("The second factors of principal {PrincipalId} were reset by the {Actor}: {Removed} removed.", principal.Id, actor, removed);
        return removed;
    }

    private async Task<Result<Enrolled>> SaveAsync(AuthenticationChallenge challenge, Principal principal, Factor factor, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var first = !await HasAnyAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        context.Factors.Add(factor);
        principal.MfaSatisfied();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var codes = first ? await recoveryCodes.ReplaceAsync(principal.Id, cancellationToken).ConfigureAwait(false) : null;
        await securityEvents.RecordAsync(principal, SecurityEventKinds.FactorEnrolled, SecurityEventActors.Self, null, null, Details(factor), cancellationToken, actorId: principal.Id).ConfigureAwait(false);
        if (challenge.Purpose != ChallengePurposes.Enrolment)
        {
            // An enrolment at sign-in goes on to start the session, which ends the challenge with it.
            await challenges.EndAsync(challenge, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Principal {PrincipalId} enrolled a factor ({Kind}).", principal.Id, factor.Kind);
        return new Enrolled(factor, codes);
    }

    /// <summary>The method that proved the person, or null. Nothing here saves; the caller's transaction does.</summary>
    private async Task<string?> CheckAsync(Principal principal, SecondFactorProof proof, AuthenticationChallenge? challenge, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (proof.Passkey is { ValueKind: JsonValueKind.Object } credential)
        {
            if (challenge?.WebAuthnOptions is not { } options || Passkeys.CredentialIdOf(credential) is not { } credentialId)
            {
                return null;
            }

            var passkey = await context.Factors.SingleOrDefaultAsync(f => f.PrincipalId == principal.Id && f.Kind == FactorKinds.Passkey && f.CredentialId == credentialId, cancellationToken).ConfigureAwait(false);
            if (passkey is null || await passkeys.VerifyAsync(options, credential, passkey, cancellationToken).ConfigureAwait(false) is not { } count)
            {
                return null;
            }

            passkey.UsedPasskey(count, now);
            return FactorKinds.Passkey;
        }

        if (!string.IsNullOrWhiteSpace(proof.Code))
        {
            var app = await context.Factors.SingleOrDefaultAsync(f => f.PrincipalId == principal.Id && f.Kind == FactorKinds.AuthenticatorApp, cancellationToken).ConfigureAwait(false);
            if (app is null)
            {
                return null;
            }

            var secret = protector.Open(app.SecretCiphertext!, principal.Id);
            try
            {
                if (Totp.Match(secret, proof.Code, now, app.LastUsedStep) is not { } step)
                {
                    return null;
                }

                app.UsedCode(step, now);
                return FactorKinds.AuthenticatorApp;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        if (!string.IsNullOrWhiteSpace(proof.RecoveryCode) && await recoveryCodes.UseAsync(principal.Id, proof.RecoveryCode, cancellationToken).ConfigureAwait(false))
        {
            return RecoveryCodeMethod;
        }

        return null;
    }

    /// <summary>A wrong second factor, counted; the tenth in a row locks the login. Two at once race on the person's row: the loser counts again.</summary>
    private async Task<Error> FailAsync(Principal principal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var locked = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                locked = principal.RecordSecondFactorFailure(now);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                await context.Entry(principal).ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, principal.Id, principal.LoginId, Reason: "wrong_second_factor"), cancellationToken).ConfigureAwait(false);
        logger.LogWarning("A second factor of principal {PrincipalId} was wrong ({Failures} in a row).", principal.Id, principal.SecondFactorFailures);
        if (!locked)
        {
            return principal.IsCooling(now) ? Cooling(principal, now) : Error.Unauthorized(IdentityErrors.SecondFactorInvalid, InvalidMessage);
        }

        var until = await throttles.LockAsync(principal.LoginId, cancellationToken).ConfigureAwait(false);
        await challenges.EndAllOfAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["locked_until"] = until.ToString("O", CultureInfo.InvariantCulture),
            ["after"] = "second_factor",
        };
        await securityEvents.RecordAsync(principal, SecurityEventKinds.LoginLocked, SecurityEventActors.System, null, null, details, cancellationToken, actorId: principal.Id).ConfigureAwait(false);
        logger.LogWarning("Login identifier {LoginId} locked until {LockedUntil} after ten wrong second factors.", principal.LoginId, until);
        return Error.Unauthorized(IdentityErrors.LoginLocked, LoginService.LockedMessage);
    }

    private static Error Cooling(Principal principal, DateTimeOffset now)
    {
        var seconds = (int)Math.Ceiling((principal.SecondFactorCoolingUntil!.Value - now).TotalSeconds);
        return new Error(ErrorKind.Unauthorized, IdentityErrors.SecondFactorCooling, $"Too many wrong codes in a row. Wait {seconds} seconds, then try again.")
        {
            Extensions = new Dictionary<string, object>(StringComparer.Ordinal) { ["retry_after_seconds"] = seconds },
        };
    }

    private async Task<Error?> AllowedAsync(string kind, CancellationToken cancellationToken)
    {
        var rules = await policy.RulesAsync(cancellationToken).ConfigureAwait(false);
        return rules.Allows(kind)
            ? null
            : Error.Forbidden(IdentityErrors.FactorNotAllowed, kind == FactorKinds.Passkey ? "Your organisation does not allow passkeys. Use an authenticator app." : "Your organisation does not allow authenticator apps. Use a passkey.");
    }

    /// <summary>The name an authenticator app shows beside the code: the product and the tenant's address.</summary>
    private string Issuer() => $"IronSaaS ({tenant.PrimaryHostname})";

    private static Dictionary<string, string> Details(Factor factor) => new(StringComparer.Ordinal)
    {
        ["factor_id"] = factor.Id.ToString("D"),
        ["kind"] = factor.Kind,
        ["name"] = factor.Name,
    };
}
