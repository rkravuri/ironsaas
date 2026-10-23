using System.Text.Json;
using Iron.Kernel.Changes.Contracts;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Changes.Data;

/// <summary>
/// A change of sensitive configuration that waits for a second administrator (TF-068, ADR-0052): what it changes and how,
/// its state before and after as people read them, what the owning module needs to apply it, who asked and why, the
/// permission and the place an approver must hold it, and how it ended. It is the first form of TF-247's change request:
/// one subject, one pending change at a time, applied only when the subject still holds the values it was asked against.
/// </summary>
public sealed class PendingChange : AuditedEntity, ITenantWide
{
    /// <summary>The record type of a pending change in the audit trail and in its signatures.</summary>
    public const string RecordType = "configuration_change";

    public const int TitleMaxLength = 300;
    public const int ReasonMaxLength = 1000;

    private PendingChange()
    {
        Area = string.Empty;
        Kind = string.Empty;
        Subject = string.Empty;
        Title = string.Empty;
        BeforeJson = "null";
        AfterJson = "null";
        PayloadJson = "null";
        Permission = string.Empty;
        RequesterName = string.Empty;
        Status = PendingChangeStates.Pending;
    }

    public PendingChange(ChangeProposal proposal, Guid requestedById, string requesterName, DateTimeOffset expiresAt)
        : this()
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Area = proposal.Area;
        Kind = proposal.Kind;
        Subject = proposal.Subject;
        Title = proposal.Title.Length > TitleMaxLength ? proposal.Title[..TitleMaxLength] : proposal.Title;
        BeforeJson = proposal.Before.GetRawText();
        AfterJson = proposal.After.GetRawText();
        PayloadJson = proposal.Payload.GetRawText();
        Permission = proposal.Permission;
        ScopeUnitId = proposal.Scope.Kind == AccessScopeKind.Unit ? proposal.Scope.UnitId : null;
        Reason = string.IsNullOrWhiteSpace(proposal.Reason) ? null : proposal.Reason.Trim();
        RequestedById = requestedById;
        RequesterName = requesterName;
        ExpiresAt = expiresAt;
        Reviews = 1;
    }

    /// <summary>The area of configuration (<see cref="ConfigurationAreas"/>).</summary>
    public string Area { get; private set; }

    /// <summary>What the owning module does on approval, such as <c>authz.role.update</c>.</summary>
    public string Kind { get; private set; }

    /// <summary>What it changes, such as <c>role:{id}</c>: one pending change per subject.</summary>
    public string Subject { get; private set; }

    public string Title { get; private set; }

    public string BeforeJson { get; private set; }

    public string AfterJson { get; private set; }

    public string PayloadJson { get; private set; }

    /// <summary>The permission an approver must hold, at <see cref="ScopeUnitId"/> or tenant-wide.</summary>
    public string Permission { get; private set; }

    public Guid? ScopeUnitId { get; private set; }

    public string? Reason { get; private set; }

    public Guid RequestedById { get; private set; }

    public string RequesterName { get; private set; }

    public string Status { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>How many reviews the change has had: one, and one more each time something else changed its subject.</summary>
    public int Reviews { get; private set; }

    /// <summary>When it last went back for a new review, and what had changed meanwhile, in words.</summary>
    public DateTimeOffset? RestartedAt { get; private set; }

    public string? RestartNote { get; private set; }

    public Guid? DecidedById { get; private set; }

    public string? DeciderName { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? DecisionReason { get; private set; }

    /// <summary>The approver's or rejecter's electronic signature, where the tenant asks for one in this area.</summary>
    public Guid? SignatureId { get; private set; }

    public AccessScope Scope => ScopeUnitId is { } unit ? AccessScope.Unit(unit) : AccessScope.Tenant;

    public JsonElement Before => Parse(BeforeJson);

    public JsonElement After => Parse(AfterJson);

    public JsonElement Payload => Parse(PayloadJson);

    public bool IsPending => Status == PendingChangeStates.Pending;

    public HeldChange Held() => new(Id, Area, Kind, Subject, Before, After, Payload, RequestedById, Reason);

    /// <summary>The subject changed since the request: the change keeps its after and asks for a new review against the values it holds now.</summary>
    public void Restart(JsonElement current, string note, DateTimeOffset at)
    {
        BeforeJson = current.GetRawText();
        Reviews++;
        RestartedAt = at;
        RestartNote = note;
    }

    public void Decide(string status, Guid deciderId, string deciderName, DateTimeOffset at, string? reason, Guid? signatureId)
    {
        Status = status;
        DecidedById = deciderId;
        DeciderName = deciderName;
        DecidedAt = at;
        DecisionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        SignatureId = signatureId;
    }

    /// <summary>Withdrawn by its requester, or expired: nobody decided it.</summary>
    public void End(string status, DateTimeOffset at)
    {
        Status = status;
        DecidedAt = at;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
