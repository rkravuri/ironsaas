using System.Globalization;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Handover;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>Bound from the <c>Impersonation</c> section: what the fleet hands the <c>impersonate</c> command (ADR-0049).</summary>
public sealed class OperatorImpersonationOptions
{
    public const string SectionName = "Impersonation";

    /// <summary>The person to sign in as.</summary>
    public string? LoginId { get; set; }

    /// <summary>The operator by the control plane's id and by name: the tenant's audit trail and the person's activity name them.</summary>
    public Guid? OperatorId { get; set; }

    public string? Operator { get; set; }

    public string? Reason { get; set; }

    /// <summary>How long, up to the tenant's maximum; the maximum when unset.</summary>
    public int? Minutes { get; set; }
}

/// <summary>Bound from the <c>BreakGlass</c> section: what the fleet hands <c>break-glass-open</c> and <c>break-glass-close</c> (ADR-0049).</summary>
public sealed class BreakGlassOptions
{
    public const string SectionName = "BreakGlass";

    /// <summary>The control plane's id of the access.</summary>
    public Guid? Id { get; set; }

    /// <summary>The operator who asked, and the one who approved, by name.</summary>
    public string? Operator { get; set; }

    public string? Approver { get; set; }

    public string? Ticket { get; set; }

    public string? Reason { get; set; }

    /// <summary>When the access ends by itself.</summary>
    public DateTimeOffset? Until { get; set; }

    /// <summary>For the close: the account the open created.</summary>
    public string? Account { get; set; }
}

/// <summary>
/// An operator's emergency access to a unit (TF-026, ADR-0049). <c>break-glass-open</c> creates a time-limited account with
/// the predefined role <c>break-glass</c>, records both operators, the ticket and the reason in the tenant's audit trail,
/// and opens a one-time sign-in link for the operator who asked. <c>break-glass-close</c>, at the time limit or when an
/// operator ends it early, deactivates the account, ends its sessions, and makes the review: every action of the account
/// from the audit trail, in order (TF-026 AC3). The unit also refuses the account after its time by itself.
/// </summary>
public sealed class BreakGlassAccounts(
    IdentityDbContext context,
    SessionService sessions,
    IOrganisationUnits units,
    IProvidedAssignments assignments,
    IAuditTrail trail,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    TimeProvider time)
{
    /// <summary>The source of the account's role, so nothing a person granted is ever touched by break-glass.</summary>
    public const string AssignmentSource = "break_glass";

    /// <summary>The account's login identifier for an access, by the rule both planes know.</summary>
    public static string LoginIdOf(Guid accessId) => BreakGlassAccountNames.LoginIdOf(accessId);

    public async Task<Result<(Principal Account, string Token, DateTimeOffset ExpiresAt)>> OpenAsync(BreakGlassOptions request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id is not { } id || string.IsNullOrWhiteSpace(request.Operator) || string.IsNullOrWhiteSpace(request.Approver) || string.IsNullOrWhiteSpace(request.Ticket) || string.IsNullOrWhiteSpace(request.Reason) || request.Until is not { } until)
        {
            return Error.Validation(IdentityErrors.BreakGlassInvalid, "Break-glass needs BreakGlass:Id, Operator, Approver, Ticket, Reason and Until.");
        }

        var now = time.GetUtcNow();
        if (until <= now)
        {
            return Error.Validation(IdentityErrors.BreakGlassInvalid, "The access's time limit has passed already.");
        }

        if (string.Equals(request.Operator.Trim(), request.Approver.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Error.Forbidden(IdentityErrors.BreakGlassSameOperator, "Break-glass needs two operators: the one who approves is not the one who asked.");
        }

        var loginId = LoginIdOf(id);
        if (await context.Principals.AnyAsync(p => p.LoginId == loginId, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(IdentityErrors.BreakGlassInvalid, "This access has opened its account already.");
        }

        var root = await units.RootAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The unit has no root organisation unit; run the seed first.");
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var account = new Principal(loginId, PrincipalType.User, $"Break-glass access ({request.Operator.Trim()})", null);
        account.OpenForBreakGlass(until);
        account.SetHomeUnit(root.Id);
        context.Principals.Add(account);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var granted = await assignments.SyncAsync(account.Id, AssignmentSource, [new ProvidedRole(SystemRoles.BreakGlass, RoleScope.Tenant)], cancellationToken).ConfigureAwait(false);
        if (granted.IsFailure || granted.Value.Added.Count == 0)
        {
            return granted.IsFailure ? granted.Error : Error.Conflict(IdentityErrors.BreakGlassInvalid, $"The role {SystemRoles.BreakGlass} could not be given: {string.Join("; ", granted.Value.Skipped)}. Run the seed (make update-unit).");
        }

        var token = SecureTokens.New();
        var link = new SignInLink(account.Id, SignInLinkPurposes.BreakGlass, SecureTokens.Hash(token), now);
        context.SignInLinks.Add(link);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["break_glass_id"] = id.ToString("D"),
            ["operator"] = request.Operator.Trim(),
            ["approver"] = request.Approver.Trim(),
            ["ticket"] = request.Ticket.Trim(),
            ["until"] = until.ToString("O", CultureInfo.InvariantCulture),
            ["role"] = SystemRoles.BreakGlass,
        };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.BreakGlassOpened, AuditOutcome.Success, SystemPrincipal.SystemId, account.LoginId, details, UserService.RecordType, account.Id, request.Reason.Trim(), Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (account, token, link.ExpiresAt);
    }

    /// <summary>
    /// Ends the access: the account is deactivated, its sessions and links end, and the review lists every action the audit
    /// trail recorded of it, oldest first. Closing an account that is closed already answers its review again.
    /// </summary>
    public async Task<Result<IReadOnlyList<AuditTrailEntry>>> CloseAsync(string loginId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var normalised = Principal.NormaliseLoginId(loginId);
        var account = await context.Principals.SingleOrDefaultAsync(p => p.LoginId == normalised, cancellationToken).ConfigureAwait(false);
        if (account is null || account.BreakGlassUntil is null)
        {
            return Error.NotFound(IdentityErrors.BreakGlassInvalid, $"'{normalised}' is not a break-glass account of this tenant.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var ended = 0;
        if (!account.IsDeactivated)
        {
            account.Deactivate();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ended = await sessions.EndAllAsync(account.Id, SessionEndReasons.Revoked, cancellationToken).ConfigureAwait(false);
            await context.SignInLinks.Where(l => l.PrincipalId == account.Id && l.UsedAt == null).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        var actions = await trail.ActionsOfAsync(account.Id, account.CreatedAt, cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["actions"] = actions.Count.ToString(CultureInfo.InvariantCulture),
            ["sessions_ended"] = ended.ToString(CultureInfo.InvariantCulture),
        };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.BreakGlassClosed, AuditOutcome.Success, SystemPrincipal.SystemId, account.LoginId, details, UserService.RecordType, account.Id, reason.Trim(), Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(actions);
    }
}

/// <summary>
/// The operator's <c>impersonate</c> command (TF-025, ADR-0049): the fleet runs it in the unit with the person, the
/// operator and the reason; the link it opens goes to the store's one-time place for the operator who asked, never to
/// the output. Exit code 0 when the link was handed over, 1 when the request is refused, with the reason logged.
/// </summary>
public sealed class OperatorImpersonationCommand(OperatorImpersonationOptions options, ImpersonationService impersonation, IOperatorHandover handover, ILogger<OperatorImpersonationCommand> logger) : IUnitCommand
{
    public string Name => DatabaseCommands.Impersonate;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!handover.IsAvailable)
        {
            logger.LogError("No impersonation link was issued: the command has no handover place, which only the fleet gives.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(options.LoginId) || options.OperatorId is not { } operatorId || string.IsNullOrWhiteSpace(options.Operator) || string.IsNullOrWhiteSpace(options.Reason))
        {
            logger.LogError("No impersonation link was issued: the command needs Impersonation:LoginId, OperatorId, Operator and Reason.");
            return 1;
        }

        var result = await impersonation.IssueOperatorLinkAsync(options.LoginId, operatorId, options.Operator, options.Reason, options.Minutes, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            logger.LogError("No impersonation link was issued ({Code}): {Message}", result.Error.Code, result.Error.Message);
            return 1;
        }

        var (token, expiresAt, person) = result.Value;
        await handover.DepositAsync(new OperatorHandoverItem(OperatorHandoverItem.Impersonation, person.LoginId, Links.SignIn(token), expiresAt), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Handed over an impersonation link for {LoginId} to the operator who asked; it works once, until {ExpiresAt}.", person.LoginId, expiresAt);
        return 0;
    }
}

/// <summary>The fleet's <c>break-glass-open</c> command: the account, its role, the audit, and the link handed over once (TF-026 AC1).</summary>
public sealed class BreakGlassOpenCommand(BreakGlassOptions options, BreakGlassAccounts accounts, IOperatorHandover handover, ILogger<BreakGlassOpenCommand> logger) : IUnitCommand
{
    public string Name => DatabaseCommands.BreakGlassOpen;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!handover.IsAvailable)
        {
            logger.LogError("No break-glass account was opened: the command has no handover place, which only the fleet gives.");
            return 1;
        }

        var result = await accounts.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            logger.LogError("No break-glass account was opened ({Code}): {Message}", result.Error.Code, result.Error.Message);
            return 1;
        }

        var (account, token, expiresAt) = result.Value;
        await handover.DepositAsync(new OperatorHandoverItem(OperatorHandoverItem.BreakGlass, account.LoginId, Links.SignIn(token), expiresAt), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Handed over a break-glass sign-in link for {LoginId} to the operator who asked; the access ends at {Until}.", account.LoginId, options.Until);
        return 0;
    }
}

/// <summary>
/// The fleet's <c>break-glass-close</c> command (TF-026 AC3): the account ends, and its review, the full list of its
/// actions, goes to the store's one-time place for the control plane to keep. The output says how many actions, never what.
/// </summary>
public sealed class BreakGlassCloseCommand(BreakGlassOptions options, BreakGlassAccounts accounts, IOperatorHandover handover, TimeProvider time, ILogger<BreakGlassCloseCommand> logger) : IUnitCommand
{
    public string Name => DatabaseCommands.BreakGlassClose;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!handover.IsAvailable || string.IsNullOrWhiteSpace(options.Account) || string.IsNullOrWhiteSpace(options.Reason))
        {
            logger.LogError("The break-glass account was not closed: the command needs its handover place, BreakGlass:Account and BreakGlass:Reason.");
            return 1;
        }

        var result = await accounts.CloseAsync(options.Account, options.Reason, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            logger.LogError("The break-glass account was not closed ({Code}): {Message}", result.Error.Code, result.Error.Message);
            return 1;
        }

        var review = JsonSerializer.Serialize(result.Value.Select(a => new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["at"] = a.At.ToString("O", CultureInfo.InvariantCulture),
            ["event_type"] = a.EventType,
            ["outcome"] = a.Outcome,
            ["target"] = a.Target,
            ["record_type"] = a.RecordType,
            ["record_id"] = a.RecordId?.ToString("D"),
            ["reason"] = a.Reason,
        }));
        await handover.DepositAsync(new OperatorHandoverItem(OperatorHandoverItem.BreakGlassReview, options.Account, string.Empty, time.GetUtcNow().AddDays(1)) { Payload = review }, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Handed over a break-glass review of {Count} action(s) for {LoginId}.", result.Value.Count, options.Account);
        return 0;
    }
}
