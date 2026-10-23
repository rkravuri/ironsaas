using System.Globalization;
using System.Text.Json;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Changes.Contracts;
using Iron.Kernel.Changes.Data;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data.History;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Changes.Services;

/// <summary>Which pending changes a list shows: the person's own, the ones they may decide, or every one (<c>changes.changes.read</c>).</summary>
public enum ChangeView
{
    Mine,
    ToApprove,
    All,
}

/// <summary>A pending change as the console shows it, with whether the person looking may decide it and whether a signature decides it.</summary>
public sealed record ChangeItem(PendingChange Change, bool MayDecide, bool Signed);

/// <summary>
/// The decisions on pending changes (TF-068, ADR-0052). Another administrator, holding the change's permission at its
/// place, approves or rejects with a reason and a confirmation that it is them, or with an electronic signature where
/// the tenant asks (TF-063). Never the requester (AC2), nobody signed in as someone else, acting for someone, a service
/// account or a break-glass account; each such attempt is audited before anything is checked. On approval the subject's
/// current state is compared with the one the change was asked against: when something else changed it meanwhile, the
/// change goes back for a new review; otherwise the owning module applies it, as the approver, in the transaction that
/// records the decision, whose event names both people (AC3). A change waits at most the tenant's period, then expires.
/// </summary>
public sealed class PendingChanges(
    ChangesDbContext context,
    ICurrentPrincipal current,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    ISettings settings,
    IEnumerable<IConfigurationChangeApplier> appliers,
    IPermissionEvaluator evaluator,
    PermissionCatalogue catalogue,
    IRecentAuthentication recent,
    ISignatures signatures,
    IChangeReason changeReason,
    ChangeApplication application,
    TimeProvider time,
    ILogger<PendingChanges> logger,
    IPrincipals? principals = null)
{
    public const string ApproveOperation = "approve";

    public async Task<IReadOnlyList<ChangeItem>> ListAsync(ChangeView view, string? status, CancellationToken cancellationToken)
    {
        await ExpireAsync(cancellationToken).ConfigureAwait(false);
        var query = context.Changes.AsNoTracking();
        query = view switch
        {
            ChangeView.Mine => query.Where(c => c.RequestedById == current.Id),
            ChangeView.ToApprove => query.Where(c => c.Status == PendingChangeStates.Pending && c.RequestedById != current.Id),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(c => c.Status == status);
        }

        var found = await query.OrderByDescending(c => c.CreatedAt).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        var signedAreas = await SignedAreasAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<ChangeItem>();
        foreach (var change in found)
        {
            var mayDecide = change.IsPending && change.RequestedById != current.Id && await HoldsAsync(change, cancellationToken).ConfigureAwait(false);
            if (view == ChangeView.ToApprove && !mayDecide)
            {
                continue;
            }

            items.Add(new ChangeItem(change, mayDecide, signedAreas.Contains(change.Area)));
        }

        return items;
    }

    /// <summary>The change, when the person may see it: theirs, one they may decide, or any with <c>changes.changes.read</c>.</summary>
    public async Task<ChangeItem?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await ExpireAsync(cancellationToken).ConfigureAwait(false);
        var change = await context.Changes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (change is null)
        {
            return null;
        }

        var holds = await HoldsAsync(change, cancellationToken).ConfigureAwait(false);
        if (change.RequestedById != current.Id && !holds && !await ReadsAllAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var signedAreas = await SignedAreasAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeItem(change, change.IsPending && change.RequestedById != current.Id && holds, signedAreas.Contains(change.Area));
    }

    public Task<Result<ChangeItem>> ApproveAsync(Guid id, string? reason, SigningCredentials? credentials, CancellationToken cancellationToken) =>
        DecideAsync(id, approve: true, reason, credentials, cancellationToken);

    public Task<Result<ChangeItem>> RejectAsync(Guid id, string? reason, SigningCredentials? credentials, CancellationToken cancellationToken) =>
        DecideAsync(id, approve: false, reason, credentials, cancellationToken);

    /// <summary>The requester takes their change back before anyone decided it.</summary>
    public async Task<Result<ChangeItem>> WithdrawAsync(Guid id, CancellationToken cancellationToken)
    {
        var change = await context.Changes.SingleOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (change is null || change.RequestedById != current.Id)
        {
            return change is null || !await ReadsAllAsync(cancellationToken).ConfigureAwait(false)
                ? Error.NotFound(ChangesErrors.NotFound, "There is no such change waiting for approval.")
                : Error.Forbidden(ChangesErrors.NotYours, "Only the person who asked for a change may withdraw it. Reject it instead, with a reason.");
        }

        if (!change.IsPending)
        {
            return NotPending(change);
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        change.End(PendingChangeStates.Withdrawn, time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(ChangesAuditEvents.ChangeWithdrawn, AuditOutcome.Success, current.Id, change.Title, Details(change), PendingChange.RecordType, change.Id), cancellationToken).ConfigureAwait(false);
        await DiscardAsync(change, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeItem(change, false, false);
    }

    /// <summary>Ends every pending change past its time, as the system: nobody decided it, so nothing applies (ADR-0052).</summary>
    public async Task<int> ExpireAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = await context.Changes.Where(c => c.Status == PendingChangeStates.Pending && c.ExpiresAt <= now).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var change in due)
        {
            await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            change.End(PendingChangeStates.Expired, now);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEvent(ChangesAuditEvents.ChangeExpired, AuditOutcome.Success, SystemPrincipal.SystemId, change.Title, Details(change), PendingChange.RecordType, change.Id), cancellationToken).ConfigureAwait(false);
            await DiscardAsync(change, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("The configuration change {ChangeId} expired after waiting for approval until {ExpiresAt}.", change.Id, change.ExpiresAt);
        }

        return due.Count;
    }

    private async Task<Result<ChangeItem>> DecideAsync(Guid id, bool approve, string? reason, SigningCredentials? credentials, CancellationToken cancellationToken)
    {
        await ExpireAsync(cancellationToken).ConfigureAwait(false);
        var change = await context.Changes.SingleOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (change is null)
        {
            return Error.NotFound(ChangesErrors.NotFound, "There is no such change waiting for approval.");
        }

        if (!change.IsPending)
        {
            return NotPending(change);
        }

        if (!approve && string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ChangesErrors.ReasonRequired, "Say why you reject the change; the requester reads it.", new FieldError("reason", "required", "Give a reason."));
        }

        if (reason is { Length: > PendingChange.ReasonMaxLength })
        {
            return Error.Validation(ChangesErrors.ReasonRequired, $"A reason is at most {PendingChange.ReasonMaxLength} characters.", new FieldError("reason", "too_long", $"At most {PendingChange.ReasonMaxLength} characters."));
        }

        // Who may never decide, refused and recorded before any credential or permission is looked at (TF-068 AC2).
        if (await NeverAsync(change, cancellationToken).ConfigureAwait(false) is { } never)
        {
            var details = Details(change);
            details["decision"] = approve ? "approve" : "reject";
            details["refused"] = never.Code;
            await audit.WriteAsync(new AuditEvent(ChangesAuditEvents.ApprovalRefused, AuditOutcome.Denied, current.Id, change.Title, details, PendingChange.RecordType, change.Id, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
            return never;
        }

        if (!await HoldsAsync(change, cancellationToken, audited: true).ConfigureAwait(false))
        {
            return Error.Forbidden(PermissionErrors.PermissionDenied, $"Deciding this change needs {change.Permission} where it applies, which you do not hold.");
        }

        // A signature where the tenant asks for one in this area, or a recent confirmation that it is the person (TF-248).
        var signed = (await SignedAreasAsync(cancellationToken).ConfigureAwait(false)).Contains(change.Area)
            || (await signatures.PolicyAsync(PendingChange.RecordType, cancellationToken).ConfigureAwait(false)).Required;
        SignerProof? proof = null;
        if (signed)
        {
            var proved = await signatures.AuthenticateAsync(PendingChange.RecordType, credentials ?? SigningCredentials.None, cancellationToken).ConfigureAwait(false);
            if (proved.IsFailure)
            {
                return proved.Error;
            }

            proof = proved.Value;
        }
        else if (recent.Require() is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        var name = await NameAsync(current.Id, cancellationToken).ConfigureAwait(false);
        var applier = appliers.FirstOrDefault(a => a.Handles(change.Kind));
        if (applier is null)
        {
            logger.LogError("No module applies configuration changes of the kind {Kind} (change {ChangeId}).", change.Kind, change.Id);
            return Error.Unexpected(ChangesErrors.Unknown, "This host cannot apply this kind of change.");
        }

        if (approve)
        {
            var state = await applier.CurrentAsync(change.Held(), cancellationToken).ConfigureAwait(false);
            if (!ChangeJson.Same(state, change.Before))
            {
                return await RestartAsync(change, state, cancellationToken).ConfigureAwait(false);
            }
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        if (approve)
        {
            application.Applying = change.Id;
            changeReason.Reason = Trimmed($"Approved configuration change {change.Id:D}{(change.Reason is null ? string.Empty : ": " + change.Reason)}");
            Result applied;
            try
            {
                applied = await applier.ApplyAsync(change.Held(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                application.Applying = null;
                changeReason.Reason = null;
            }

            if (applied.IsFailure)
            {
                // The module refused, as the approver: nothing applies, and the change stays for another decision.
                return applied.Error;
            }
        }
        else
        {
            await DiscardAsync(change, cancellationToken).ConfigureAwait(false);
        }

        Guid? signatureId = null;
        if (proof is not null)
        {
            var signature = await signatures.RecordAsync(proof, PendingChangeRecords.Snapshot(change), approve ? SignatureMeanings.Approved : SignatureMeanings.Rejected, reason, cancellationToken).ConfigureAwait(false);
            if (signature.IsFailure)
            {
                return signature.Error;
            }

            signatureId = signature.Value.Id;
        }

        change.Decide(approve ? PendingChangeStates.Applied : PendingChangeStates.Rejected, current.Id, name, time.GetUtcNow(), reason, signatureId);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var decided = Details(change);
        decided["requested_by"] = change.RequestedById.ToString("D");
        decided["requester_name"] = change.RequesterName;
        decided["decided_by"] = current.Id.ToString("D");
        decided["decider_name"] = name;
        if (signatureId is { } signatureValue)
        {
            decided["signature_id"] = signatureValue.ToString("D");
        }

        await audit.WriteAsync(
            new AuditEvent(approve ? ChangesAuditEvents.ChangeApproved : ChangesAuditEvents.ChangeRejected, AuditOutcome.Success, current.Id, change.Title, decided, PendingChange.RecordType, change.Id, string.IsNullOrWhiteSpace(reason) ? change.Reason : reason.Trim()),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (approve)
        {
            // Only now is the change in the database, for a module that reads it back into a cache (the settings).
            await applier.AppliedAsync(change.Held(), cancellationToken).ConfigureAwait(false);
        }

        return new ChangeItem(change, false, signed);
    }

    /// <summary>The subject changed since the request: the change takes the values the subject holds now as its before and needs a new review.</summary>
    private async Task<Result<ChangeItem>> RestartAsync(PendingChange change, JsonElement state, CancellationToken cancellationToken)
    {
        var note = $"Changed since the request: it was {Shorten(change.BeforeJson)} and is now {Shorten(state.GetRawText())}.";
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        change.Restart(state, note, time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = Details(change);
        details["reviews"] = change.Reviews.ToString(CultureInfo.InvariantCulture);
        details["note"] = note;
        await audit.WriteAsync(new AuditEvent(ChangesAuditEvents.ChangeRestarted, AuditOutcome.Success, current.Id, change.Title, details, PendingChange.RecordType, change.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Error.Conflict(ChangesErrors.Stale, "Something else changed this since the request, so it did not apply. It is back for a new review: compare the current values with the change and decide again.");
    }

    private async Task<Error?> NeverAsync(PendingChange change, CancellationToken cancellationToken)
    {
        if (current.ImpersonatorId is not null)
        {
            return Error.Forbidden(ChangesErrors.Impersonation, "Nobody approves or rejects a change while signed in as someone else.");
        }

        if (change.RequestedById == current.Id)
        {
            return Error.Forbidden(ChangesErrors.Own, "You asked for this change, so another administrator must decide it. You may withdraw it.");
        }

        if (current.ActingForId is not null)
        {
            return Error.Forbidden(ChangesErrors.ActingFor, "Nobody decides a change while acting for someone else. Stop acting for them first.");
        }

        if (current.IsBreakGlass)
        {
            return Error.Forbidden(ChangesErrors.BreakGlass, "A break-glass account repairs a tenant; it never approves its configuration changes.");
        }

        if (principals is not null && (await principals.FindAsync([current.Id], cancellationToken).ConfigureAwait(false)).TryGetValue(current.Id, out var me) && me.Type != PrincipalKind.User)
        {
            return Error.Forbidden(ChangesErrors.ServiceAccount, "A change is decided by a person, never by a service account.");
        }

        return null;
    }

    /// <summary>Whether the person holds the change's permission where it applies (TF-068: the same scope as the requester needed).</summary>
    private async Task<bool> HoldsAsync(PendingChange change, CancellationToken cancellationToken, bool audited = false)
    {
        if (catalogue.Find(change.Permission) is not { } permission)
        {
            return false;
        }

        var request = new AccessRequest(current.Id, permission, ApproveOperation, $"/api/v1/changes/{change.Id:D}/approve") { Scope = change.Scope };
        return audited
            ? (await evaluator.EvaluateAsync(request, cancellationToken).ConfigureAwait(false)).Allowed
            : (await evaluator.ExplainAsync(request, cancellationToken).ConfigureAwait(false)).Allowed;
    }

    private async Task<bool> ReadsAllAsync(CancellationToken cancellationToken) =>
        (await evaluator.ExplainAsync(new AccessRequest(current.Id, ChangesPermissions.Read, "read", "/api/v1/changes"), cancellationToken).ConfigureAwait(false)).Allowed;

    private async Task<IReadOnlyList<string>> SignedAreasAsync(CancellationToken cancellationToken) =>
        ChangesSettings.AreasOf(await settings.GetAsync(ChangesSettings.SignedAreas, SettingContext.Tenant, cancellationToken).ConfigureAwait(false));

    private async Task DiscardAsync(PendingChange change, CancellationToken cancellationToken)
    {
        if (appliers.FirstOrDefault(a => a.Handles(change.Kind)) is { } applier)
        {
            await applier.DiscardAsync(change.Held(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> NameAsync(Guid id, CancellationToken cancellationToken)
    {
        if (principals is null)
        {
            return id.ToString("D");
        }

        var found = await principals.FindAsync([id], cancellationToken).ConfigureAwait(false);
        return found.TryGetValue(id, out var summary) ? summary.DisplayName : id.ToString("D");
    }

    private static Error NotPending(PendingChange change) => change.Status == PendingChangeStates.Expired
        ? Error.Conflict(ChangesErrors.Expired, "This change expired before anyone decided it, so nothing applied. Make the change again if it is still needed.")
        : Error.Conflict(ChangesErrors.NotPending, $"This change is {change.Status} already; it can no longer be decided.");

    private static Dictionary<string, string> Details(PendingChange change) => new(StringComparer.Ordinal)
    {
        ["area"] = change.Area,
        ["kind"] = change.Kind,
        ["subject"] = change.Subject,
        ["title"] = change.Title,
    };

    private static string Shorten(string json) => json.Length <= 400 ? json : json[..400] + "…";

    private static string Trimmed(string text) => text.Length <= PendingChange.ReasonMaxLength ? text : text[..PendingChange.ReasonMaxLength];
}
