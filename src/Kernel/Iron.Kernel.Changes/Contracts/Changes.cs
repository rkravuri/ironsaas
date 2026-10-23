using System.Text.Json;
using Iron.Kernel.History;

namespace Iron.Kernel.Changes.Contracts;

public static class ChangesPermissions
{
    /// <summary>
    /// Every pending and decided change of the tenant's sensitive configuration. A requester sees their own, and an
    /// administrator the ones they may approve, without it (TF-068).
    /// </summary>
    public static readonly Permission Read = new("changes.changes.read", "See every pending and decided change of the tenant's sensitive configuration, with its requester and its approver.", Risk.Medium);

    public static IReadOnlyList<Permission> All => [Read];
}

/// <summary>Where a pending change stands (ADR-0052).</summary>
public static class PendingChangeStates
{
    /// <summary>Waiting for a second administrator; nothing applied yet.</summary>
    public const string Pending = "pending";

    /// <summary>Approved by a second administrator and applied, in one transaction.</summary>
    public const string Applied = "applied";

    public const string Rejected = "rejected";

    /// <summary>Taken back by its requester before anyone decided.</summary>
    public const string Withdrawn = "withdrawn";

    /// <summary>Nobody decided within the tenant's period (seven days unless it says otherwise).</summary>
    public const string Expired = "expired";

    public static IReadOnlyList<string> All { get; } = [Pending, Applied, Rejected, Withdrawn, Expired];
}

public static class ChangesAuditEvents
{
    /// <summary>A save of sensitive configuration was held for a second administrator (TF-068 AC1).</summary>
    public const string ChangeRequested = "changes.change_requested";

    /// <summary>A second administrator approved the change and it applied; the event names both (TF-068 AC3).</summary>
    public const string ChangeApproved = "changes.change_approved";

    public const string ChangeRejected = "changes.change_rejected";

    public const string ChangeWithdrawn = "changes.change_withdrawn";

    public const string ChangeExpired = "changes.change_expired";

    /// <summary>Something else changed the subject since the request: the change went back for a new review (ADR-0052).</summary>
    public const string ChangeRestarted = "changes.change_restarted";

    /// <summary>An approval or a rejection refused before anything was checked: the requester, an impersonator, a break-glass account (TF-068 AC2).</summary>
    public const string ApprovalRefused = "changes.approval_refused";

    /// <summary>Every event type above with the words it reads as; all are administrative actions (ADR-0051, ADR-0052).</summary>
    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new AuditEventType(ChangeRequested, "Configuration change requested").AsAdministrative(),
        new AuditEventType(ChangeApproved, "Configuration change approved and applied").AsAdministrative(),
        new AuditEventType(ChangeRejected, "Configuration change rejected").AsAdministrative(),
        new AuditEventType(ChangeWithdrawn, "Configuration change withdrawn").AsAdministrative(),
        new AuditEventType(ChangeExpired, "Configuration change expired").AsAdministrative(),
        new AuditEventType(ChangeRestarted, "Configuration change back for review").AsAdministrative(),
        new AuditEventType(ApprovalRefused, "Approval of a configuration change refused").AsAdministrative(),
    ];
}

public static class ChangesErrors
{
    /// <summary>Not a failure: the change waits for approval (HTTP 202, <see cref="ErrorKind.Held"/>).</summary>
    public const string Held = "changes.change.held";

    public const string AlreadyPending = "changes.change.already_pending";
    public const string NotFound = "changes.change.not_found";
    public const string NotPending = "changes.change.not_pending";
    public const string Expired = "changes.change.expired";

    /// <summary>The requester may not approve or reject their own change (TF-068 AC2).</summary>
    public const string Own = "changes.change.own";

    public const string Impersonation = "changes.change.impersonation";
    public const string ActingFor = "changes.change.acting_for";
    public const string BreakGlass = "changes.change.break_glass";
    public const string ServiceAccount = "changes.change.service_account";
    public const string NotYours = "changes.change.not_yours";

    /// <summary>Something else changed the subject since the request; the change is back for a new review.</summary>
    public const string Stale = "changes.change.stale";

    public const string ReasonRequired = "changes.change.reason_required";
    public const string Unknown = "changes.change.unknown_kind";
}

/// <summary>
/// The tenant's four-eyes control (TF-068, ADR-0052): which areas of sensitive configuration need a second
/// administrator, which of those approvals are electronic signatures (TF-063), and how long a change waits. While any
/// area is listed, a change of these settings needs a second administrator too.
/// </summary>
public static class ChangesSettings
{
    public static readonly Setting<JsonElement> Areas = Setting.Json(
        "changes.four_eyes.areas",
        "The areas of sensitive configuration whose changes wait for a second administrator: roles, single_sign_on, retention, mfa_and_password_policies, legal_holds, signing_policy. Empty: nothing waits.",
        "[]",
        SettingScopes.Tenant,
        SettingRule.Must(IsAreaList, "A list of areas, each once: roles, single_sign_on, retention, mfa_and_password_policies, legal_holds and signing_policy."))
        .RequiringRecentAuthentication()
        .GovernedBy(ConfigurationAreas.FourEyes);

    public static readonly Setting<JsonElement> SignedAreas = Setting.Json(
        "changes.four_eyes.signed_areas",
        "The areas whose approvals and rejections are electronic signatures (approved or rejected) rather than a confirmation that it is you.",
        "[]",
        SettingScopes.Tenant,
        SettingRule.Must(IsAreaList, "A list of areas, each once: roles, single_sign_on, retention, mfa_and_password_policies, legal_holds and signing_policy."))
        .RequiringRecentAuthentication()
        .GovernedBy(ConfigurationAreas.FourEyes);

    public static readonly Setting<int> ExpiryDays = Setting.WholeNumber(
        "changes.four_eyes.expiry_days", "How many days a change waits for a second administrator before it expires without applying.", 7, 1, 30, SettingScopes.Tenant)
        .RequiringRecentAuthentication()
        .GovernedBy(ConfigurationAreas.FourEyes);

    public static IReadOnlyList<SettingDefinition> All => [Areas, SignedAreas, ExpiryDays];

    /// <summary>The areas of a list setting's value; an area the kernel does not know is left out.</summary>
    public static IReadOnlyList<string> AreasOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).Where(ConfigurationAreas.Listed.Contains).Distinct(StringComparer.Ordinal)]
            : [];

    public static bool IsAreaList(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateArray().All(area => area.ValueKind == JsonValueKind.String && ConfigurationAreas.Listed.Contains(area.GetString()!) && seen.Add(area.GetString()!));
    }
}
