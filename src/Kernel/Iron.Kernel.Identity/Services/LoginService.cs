using System.Globalization;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// The step a sign-in goes on to when the password alone does not sign the person in (ADR-0047): the second factor, or
/// enrolment. The challenge token goes into the sign-in cookie, never into a body; the methods are the ways the person may
/// prove the second factor, or the kinds the tenant lets them enrol.
/// </summary>
public sealed record SignInStep(string Kind, string ChallengeToken, IReadOnlyList<string> Methods, DateTimeOffset ExpiresAt);

/// <summary>
/// A sign-in that succeeded: the session and its cookie token. When the password is past the tenant's maximum age there
/// is no session; <see cref="RecoveryToken"/> is the code of a recovery link the person uses to set a new one (TF-012).
/// When the password is right and a second step follows, there is no session either, and <see cref="Step"/> says which.
/// </summary>
public sealed record LoginOutcome(Principal Principal, Session? Session, string? Token, string? RecoveryToken = null)
{
    public bool PasswordExpired => RecoveryToken is not null;

    public SignInStep? Step { get; init; }

    /// <summary>A recovery code signed the person in: they are asked to enrol a new factor at once (TF-013 AC3).</summary>
    public bool EnrolNewFactor { get; init; }

    /// <summary>The recovery codes of a first enrolment at sign-in, shown once.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; init; }

    /// <summary>Where a sign-in through a provider returns the person, a path of the shell's (ADR-0048).</summary>
    public string? ReturnTo { get; init; }

    /// <summary>A re-authentication through a provider confirmed the session's person at this time; no new session (TF-248).</summary>
    public DateTimeOffset? ReauthenticatedAt { get; init; }
}

/// <summary>
/// Signing in (TF-011, TF-013, ADR-0046, ADR-0047). Every failure of the password gets the same answer, so nothing reveals
/// whether the account exists, and every failure is counted and audited with its real reason for the administrators.
/// Failures also count against the login identifier, known or not, and lock it at the tenant's threshold: while it is
/// locked even the correct password is refused. A right password signs in only a person who has no factor and need not
/// have one: a person with a factor goes on to the second step, and a person the policy requires to have one goes on to
/// enrolment, both through a challenge that creates no session: five minutes for the second step, fifteen for enrolment. A success writes the session row, the
/// login stamp and the audit event in one transaction.
/// </summary>
public sealed class LoginService(
    IdentityDbContext context,
    IPasswordHasher hasher,
    SessionService sessions,
    LoginThrottles throttles,
    PasswordPolicy policy,
    PasswordService passwords,
    SecurityEventLog securityEvents,
    FactorService factors,
    MfaPolicy mfa,
    Passkeys passkeys,
    AuthenticationChallenges challenges,
    SsoPeople ssoPeople,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    IAuditWriter audit,
    IHttpContextAccessor accessor,
    ILogger<LoginService> logger)
{
    public const string FailureMessage = "The login details are not correct.";

    public const string LockedMessage = "Too many failed sign-ins for this login. Try again later, or ask your administrator to unlock it.";

    public const string ExpiredMessage = "Your password has expired. Choose a new one to sign in.";

    public const string SignInExpiredMessage = "This sign-in took too long, or ended. Sign in again.";

    /// <summary>TF-014 AC2's words for a person who signs in only through a provider.</summary>
    public const string UseCompanyLoginMessage = "Use your company login.";

    private const int MaxAttempts = 3;

    public async Task<Result<LoginOutcome>> LoginAsync(string loginId, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        var normalised = Principal.NormaliseLoginId(loginId);

        // Locked or not is known before the account is looked up, and costs the same verification, so the answer reveals
        // nothing about the account behind the identifier.
        if (await throttles.LockedUntilAsync(normalised, cancellationToken).ConfigureAwait(false) is not null)
        {
            hasher.VerifyOrDecoy(password, null);
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, Guid.Empty, normalised, Reason: "locked"), cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Login refused for {LoginId}: the identifier is locked.", normalised);
            return Error.Unauthorized(IdentityErrors.LoginLocked, LockedMessage);
        }

        var principal = normalised.Length == 0 ? null : await context.Principals.SingleOrDefaultAsync(p => p.LoginId == normalised, cancellationToken).ConfigureAwait(false);

        if (principal is null)
        {
            hasher.VerifyOrDecoy(password, null);
            return await FailAsync(null, normalised, "unknown_login", cancellationToken).ConfigureAwait(false);
        }

        if (principal.Type != PrincipalType.User)
        {
            hasher.VerifyOrDecoy(password, principal.PasswordHash);
            return await FailAsync(principal, normalised, "not_a_user", cancellationToken).ConfigureAwait(false);
        }

        // A person who signs in only through a provider is told so before any password is looked at (TF-014 AC2,
        // ADR-0048). Whoever types the identifier learns it is such an account, which no password opens; every account a
        // password does open keeps TF-011's one answer. Nothing is counted: there is nothing here to guess.
        var ssoOnly = await ssoPeople.SsoOnlyAsync(principal, cancellationToken).ConfigureAwait(false);
        if (ssoOnly.SsoOnly)
        {
            hasher.VerifyOrDecoy(password, null);
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, principal.Id, normalised, Reason: "sso_only"), cancellationToken).ConfigureAwait(false);
            return UseCompanyLogin(ssoOnly.Provider);
        }

        if (!principal.CanLogIn)
        {
            hasher.VerifyOrDecoy(password, principal.PasswordHash);
            return await FailAsync(principal, normalised, principal.Status.ToString().ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        }

        if (!hasher.Verify(password, principal.PasswordHash!))
        {
            return await FailAsync(principal, normalised, "wrong_password", cancellationToken).ConfigureAwait(false);
        }

        // The password alone never signs in a person who has a factor (TF-013): the second step comes first, and the
        // password's age is looked at only after it.
        if (await factors.HasAnyAsync(principal.Id, cancellationToken).ConfigureAwait(false))
        {
            return await StepAsync(principal, ChallengePurposes.SecondFactor, await factors.MethodsAsync(principal.Id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        var rules = await mfa.RulesAsync(cancellationToken).ConfigureAwait(false);
        if (await mfa.IsRequiredAsync(principal, rules, cancellationToken).ConfigureAwait(false))
        {
            var now = time.GetUtcNow();
            var since = principal.MfaRequiredSince ?? principal.MfaRequiredFrom(now);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (rules.GraceDays == 0 || now >= since.AddDays(rules.GraceDays))
            {
                return await StepAsync(principal, ChallengePurposes.Enrolment, rules.Allowed, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (principal.MfaRequiredSince is not null)
        {
            principal.MfaSatisfied();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return await FinishAsync(principal, secondFactor: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The second step (TF-013): the code from the person's app, a recovery code, or their passkey's answer, while the
    /// challenge of their right password lives. A recovery code signs them in and asks them to enrol a new factor at once.
    /// </summary>
    public async Task<Result<LoginOutcome>> SecondFactorAsync(HttpContext http, SecondFactorProof proof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var found = await PendingAsync(http, ChallengePurposes.SecondFactor, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var (challenge, principal) = found.Value;
        var verified = await factors.VerifyAsync(principal, proof, challenge, cancellationToken).ConfigureAwait(false);
        if (verified.IsFailure)
        {
            return verified.Error;
        }

        await challenges.EndAsync(challenge, cancellationToken).ConfigureAwait(false);
        var outcome = await FinishAsync(principal, verified.Value, cancellationToken, challenge.ProviderId).ConfigureAwait(false);
        return outcome.IsSuccess && verified.Value == FactorService.RecoveryCodeMethod ? outcome.Value with { EnrolNewFactor = true } : outcome;
    }

    /// <summary>The options for the person's passkeys at the second step, kept with its challenge.</summary>
    public async Task<Result<string>> SecondFactorPasskeyOptionsAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var found = await PendingAsync(http, ChallengePurposes.SecondFactor, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var options = await factors.PasskeyAssertionAsync(found.Value.Challenge, found.Value.Principal.Id, cancellationToken).ConfigureAwait(false);
        return options is null ? Error.Conflict(IdentityErrors.FactorNotFound, "You have no passkey. Use the code from your app or a recovery code.") : options;
    }

    /// <summary>The challenge of an enrolment at sign-in and its person, while it lives.</summary>
    public Task<Result<(AuthenticationChallenge Challenge, Principal Principal)>> EnrolmentAsync(HttpContext http, CancellationToken cancellationToken) =>
        PendingAsync(http, ChallengePurposes.Enrolment, cancellationToken);

    /// <summary>The person enrolled their first factor at sign-in: the session starts, and the answer carries the recovery codes once.</summary>
    public async Task<Result<LoginOutcome>> EnrolledAsync(AuthenticationChallenge challenge, Principal principal, Enrolled enrolled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrolled);
        await challenges.EndAsync(challenge, cancellationToken).ConfigureAwait(false);
        var outcome = await FinishAsync(principal, enrolled.Factor.Kind, cancellationToken, challenge.ProviderId).ConfigureAwait(false);
        return outcome.IsSuccess ? outcome.Value with { RecoveryCodes = enrolled.RecoveryCodes } : outcome;
    }

    /// <summary>
    /// Options for a sign-in with a passkey alone, where the tenant allows it (ADR-0047): no person is named, and the
    /// browser offers the passkeys it holds for this tenant. The answer's challenge token goes into the sign-in cookie.
    /// </summary>
    public async Task<Result<(string Token, string Options)>> PasskeySignInOptionsAsync(CancellationToken cancellationToken)
    {
        if (!(await mfa.RulesAsync(cancellationToken).ConfigureAwait(false)).PasskeySignIn)
        {
            return Error.Forbidden(IdentityErrors.PasskeySignInOff, "Your organisation does not let you sign in with a passkey alone. Sign in with your password.");
        }

        var (challenge, token) = await challenges.StartForBrowserAsync(ChallengePurposes.PasskeySignIn, null, cancellationToken).ConfigureAwait(false);
        var options = passkeys.AssertionOptions([]);
        challenge.Expect(options);
        await challenges.SaveAsync(cancellationToken).ConfigureAwait(false);
        return (token, options);
    }

    /// <summary>
    /// A sign-in with a passkey alone: the passkey names its person and proves them, user verification included. An
    /// unknown passkey and one that does not verify get the same answer; a failure counts against the person as a wrong
    /// code does. The password's age does not matter here, since no password was used.
    /// </summary>
    public async Task<Result<LoginOutcome>> PasskeySignInAsync(HttpContext http, JsonElement credential, CancellationToken cancellationToken)
    {
        if (!(await mfa.RulesAsync(cancellationToken).ConfigureAwait(false)).PasskeySignIn)
        {
            return Error.Forbidden(IdentityErrors.PasskeySignInOff, "Your organisation does not let you sign in with a passkey alone. Sign in with your password.");
        }

        var challenge = await challenges.FindForBrowserAsync(http, [ChallengePurposes.PasskeySignIn], cancellationToken).ConfigureAwait(false);
        if (challenge is null)
        {
            return Error.Unauthorized(IdentityErrors.SignInExpired, SignInExpiredMessage);
        }

        var credentialId = Passkeys.CredentialIdOf(credential);
        var factor = credentialId is null ? null : await context.Factors.AsNoTracking().SingleOrDefaultAsync(f => f.Kind == FactorKinds.Passkey && f.CredentialId == credentialId, cancellationToken).ConfigureAwait(false);
        var principal = factor is null ? null : await context.Principals.SingleOrDefaultAsync(p => p.Id == factor.PrincipalId, cancellationToken).ConfigureAwait(false);
        if (principal is null || principal.Type != PrincipalType.User || !principal.CanLogIn)
        {
            await challenges.EndAsync(challenge, cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, principal?.Id ?? Guid.Empty, principal?.LoginId ?? string.Empty, Reason: "unknown_passkey"), cancellationToken).ConfigureAwait(false);
            return Error.Unauthorized(IdentityErrors.SecondFactorInvalid, "That passkey did not sign you in. Try another, or sign in with your password.");
        }

        if (await throttles.LockedUntilAsync(principal.LoginId, cancellationToken).ConfigureAwait(false) is not null)
        {
            await challenges.EndAsync(challenge, cancellationToken).ConfigureAwait(false);
            return Error.Unauthorized(IdentityErrors.LoginLocked, LockedMessage);
        }

        var verified = await factors.VerifyAsync(principal, new SecondFactorProof(null, null, credential), challenge, cancellationToken).ConfigureAwait(false);
        if (verified.IsFailure)
        {
            return verified.Error;
        }

        await challenges.EndAsync(challenge, cancellationToken).ConfigureAwait(false);
        return await StartSessionAsync(principal, FactorKinds.Passkey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The browser's challenge for <paramref name="purpose"/> and its person, who may still sign in; otherwise the sign-in has ended.</summary>
    private async Task<Result<(AuthenticationChallenge Challenge, Principal Principal)>> PendingAsync(HttpContext http, string purpose, CancellationToken cancellationToken)
    {
        var challenge = await challenges.FindForBrowserAsync(http, [purpose], cancellationToken).ConfigureAwait(false);
        if (challenge?.PrincipalId is not { } principalId)
        {
            return Error.Unauthorized(IdentityErrors.SignInExpired, SignInExpiredMessage);
        }

        var principal = await context.Principals.SingleOrDefaultAsync(p => p.Id == principalId, cancellationToken).ConfigureAwait(false);

        // After a provider's sign-in the person may have no password: being active is what counts then (ADR-0048).
        if (principal is null || !(challenge.ProviderId is null ? principal.CanLogIn : principal.Status == PrincipalStatus.Active))
        {
            await challenges.EndAsync(challenge, cancellationToken).ConfigureAwait(false);
            return Error.Unauthorized(IdentityErrors.SignInExpired, SignInExpiredMessage);
        }

        if (await throttles.LockedUntilAsync(principal.LoginId, cancellationToken).ConfigureAwait(false) is not null)
        {
            await challenges.EndAsync(challenge, cancellationToken).ConfigureAwait(false);
            return Error.Unauthorized(IdentityErrors.LoginLocked, LockedMessage);
        }

        return (challenge, principal);
    }

    /// <summary>
    /// A single sign-on provider signed the person in (TF-014, ADR-0048). When the provider says it used more than one
    /// factor and the tenant trusts it for that provider, that stands for this product's second factor. Otherwise the
    /// tenant's MFA policy applies as after a password: a person with a factor gives it, a person who must have one and has
    /// none enrols, and the second step or the enrolment carries the provider into the session. No password is involved,
    /// so its age is not asked.
    /// </summary>
    public async Task<Result<LoginOutcome>> ProviderSignedInAsync(Principal principal, Guid providerId, bool providerMultiFactor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (providerMultiFactor)
        {
            return await StartSessionAsync(principal, Session.ProviderMultiFactor, cancellationToken, providerId).ConfigureAwait(false);
        }

        if (await factors.HasAnyAsync(principal.Id, cancellationToken).ConfigureAwait(false))
        {
            return await StepAsync(principal, ChallengePurposes.SecondFactor, await factors.MethodsAsync(principal.Id, cancellationToken).ConfigureAwait(false), cancellationToken, providerId).ConfigureAwait(false);
        }

        var rules = await mfa.RulesAsync(cancellationToken).ConfigureAwait(false);
        if (await mfa.IsRequiredAsync(principal, rules, cancellationToken).ConfigureAwait(false))
        {
            var now = time.GetUtcNow();
            var since = principal.MfaRequiredSince ?? principal.MfaRequiredFrom(now);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (rules.GraceDays == 0 || now >= since.AddDays(rules.GraceDays))
            {
                return await StepAsync(principal, ChallengePurposes.Enrolment, rules.Allowed, cancellationToken, providerId).ConfigureAwait(false);
            }
        }
        else if (principal.MfaRequiredSince is not null)
        {
            principal.MfaSatisfied();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return await StartSessionAsync(principal, null, cancellationToken, providerId).ConfigureAwait(false);
    }

    /// <summary>The refusal of a password sign-in for a person who signs in only through a provider, naming it for the sign-in page's button.</summary>
    public static Error UseCompanyLogin(SsoProvider? provider) =>
        new(ErrorKind.Unauthorized, IdentityErrors.UseCompanyLogin, UseCompanyLoginMessage)
        {
            Extensions = provider is null
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : new Dictionary<string, object>(StringComparer.Ordinal) { ["provider_id"] = provider.Id, ["provider_name"] = provider.Name },
        };

    private async Task<Result<LoginOutcome>> StepAsync(Principal principal, string purpose, IReadOnlyList<string> methods, CancellationToken cancellationToken, Guid? providerId = null)
    {
        var (challenge, token) = await challenges.StartForBrowserAsync(purpose, principal.Id, cancellationToken).ConfigureAwait(false);
        if (providerId is { } provider)
        {
            challenge.ThroughProvider(provider);
            await challenges.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("The password of principal {PrincipalId} was right; the sign-in goes on to {Step}.", principal.Id, purpose);
        return new LoginOutcome(principal, null, null) { Step = new SignInStep(purpose, token, methods, challenge.ExpiresAt) };
    }

    /// <summary>Everything the person had to prove is proved: a password past its age gets a recovery link (TF-012), any other a session.</summary>
    private async Task<Result<LoginOutcome>> FinishAsync(Principal principal, string? secondFactor, CancellationToken cancellationToken, Guid? providerId = null)
    {
        if (providerId is not null)
        {
            return await StartSessionAsync(principal, secondFactor, cancellationToken, providerId).ConfigureAwait(false);
        }

        var rules = await policy.RulesAsync(cancellationToken).ConfigureAwait(false);
        if (principal.PasswordExpired(time.GetUtcNow(), rules.MaximumAgeDays))
        {
            return await ExpiredAsync(principal, rules, cancellationToken).ConfigureAwait(false);
        }

        return await StartSessionAsync(principal, secondFactor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<LoginOutcome>> StartSessionAsync(Principal principal, string? secondFactor, CancellationToken cancellationToken, Guid? providerId = null)
    {
        // Another attempt on the same account may save its stamp between our read and our write (the version column
        // is the concurrency token). That must never turn a correct login into an error: take the newer row and go again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                principal.RecordLogin(time.GetUtcNow());
                await throttles.ClearAsync(principal.LoginId, cancellationToken).ConfigureAwait(false);
                var (session, token) = await sessions.StartAsync(
                    principal,
                    cancellationToken,
                    created =>
                    {
                        // From here on the request acts as the user: the session row and the audit event carry it as created_by.
                        if (accessor.HttpContext is { } http)
                        {
                            RequestPrincipal.Set(http, sessions.Describe(principal, created));
                        }
                    },
                    secondFactor,
                    providerId).ConfigureAwait(false);
                var details = new Dictionary<string, string>(StringComparer.Ordinal);
                if (secondFactor is not null)
                {
                    details["second_factor"] = secondFactor;
                }

                if (providerId is { } provider)
                {
                    details["method"] = "sso";
                    details["provider_id"] = provider.ToString();
                }

                await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginSucceeded, AuditOutcome.Success, principal.Id, principal.LoginId, details.Count == 0 ? null : details, RecordType: "session", RecordId: session.Id), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                logger.LogInformation("Login succeeded for principal {PrincipalId}.", principal.Id);
                return new LoginOutcome(principal, session, token);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                logger.LogInformation("Login for principal {PrincipalId} raced another attempt; retrying ({Attempt}).", principal.Id, attempt);
                await ReloadAsync(principal, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The password is right but past its maximum age: no session, and a recovery link for the person to set a new one,
    /// issued and recorded like any other (the person proved they hold the password, and their second factor when they
    /// have one, so the link is theirs).
    /// </summary>
    private async Task<Result<LoginOutcome>> ExpiredAsync(Principal principal, PasswordRules rules, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["maximum_age_days"] = rules.MaximumAgeDays.ToString(CultureInfo.InvariantCulture) };
        var token = await passwords.OpenRecoveryAsync(principal, SecurityEventActors.System, null, "password expired", details, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, principal.Id, principal.LoginId, Reason: "password_expired"), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Login of principal {PrincipalId} found the password expired; a recovery link was issued.", principal.Id);
        return new LoginOutcome(principal, null, null, token);
    }

    /// <summary>Takes the row as it is now and forgets the session this attempt tried to add, so the next attempt starts clean.</summary>
    private async Task ReloadAsync(Principal principal, CancellationToken cancellationToken)
    {
        await context.Entry(principal).ReloadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in context.ChangeTracker.Entries<Session>().Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task<Result<LoginOutcome>> FailAsync(Principal? principal, string loginId, string reason, CancellationToken cancellationToken)
    {
        if (principal is not null)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    principal.RecordFailedLogin(time.GetUtcNow());
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
                {
                    await ReloadAsync(principal, cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateConcurrencyException exception)
                {
                    // The count is bookkeeping; the answer and the audit event matter more than the exact number.
                    logger.LogWarning(exception, "The failed-login count of principal {PrincipalId} was not updated after {Attempts} attempts.", principal.Id, MaxAttempts);
                    break;
                }
            }
        }

        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginFailed, AuditOutcome.Denied, principal?.Id ?? Guid.Empty, loginId, Reason: reason), cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Login failed for {LoginId}: {Reason}.", loginId, reason);
        var lockedUntil = await throttles.CountFailureAsync(loginId, cancellationToken).ConfigureAwait(false);
        if (lockedUntil is null)
        {
            return Error.Unauthorized(IdentityErrors.LoginInvalid, FailureMessage);
        }

        await RecordLockAsync(principal, loginId, lockedUntil.Value, cancellationToken).ConfigureAwait(false);
        return Error.Unauthorized(IdentityErrors.LoginLocked, LockedMessage);
    }

    /// <summary>
    /// The failure that locked the identifier: an audit event for the administrators, and for a person who exists a
    /// security event they see in My profile. An identifier nobody has gets the audit event alone.
    /// </summary>
    private async Task RecordLockAsync(Principal? principal, string loginId, DateTimeOffset lockedUntil, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["locked_until"] = lockedUntil.ToString("O", CultureInfo.InvariantCulture) };
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        if (principal is null)
        {
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.LoginLocked, AuditOutcome.Success, Guid.Empty, loginId, details), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await securityEvents.RecordAsync(principal, SecurityEventKinds.LoginLocked, SecurityEventActors.System, null, null, details, cancellationToken, actorId: principal.Id).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Login identifier {LoginId} locked until {LockedUntil}.", loginId, lockedUntil);
    }
}
