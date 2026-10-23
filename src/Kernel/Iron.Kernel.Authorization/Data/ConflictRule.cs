using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Authorization.Data;

/// <summary>What a side of a conflict rule names: a permission by its name, or a role by its id.</summary>
public static class ConflictSides
{
    public const string Permission = "permission";
    public const string Role = "role";

    public static bool IsKnown(string? kind) => kind is Permission or Role;
}

/// <summary>What a rule does to a grant that would create its conflict: refuse it, or allow it and list it.</summary>
public static class ConflictModes
{
    public const string Block = "block";
    public const string Warn = "warn";

    public static bool IsKnown(string? mode) => mode is Block or Warn;
}

/// <summary>
/// A segregation of duties rule (TF-036, ADR-0039): two rights one person should not hold over the same unit, such as
/// creating a supplier and approving it. Each side is a permission or a role. A block rule refuses a grant or a
/// delegation that would create the conflict, naming itself; a warn rule allows it and lists it in the report. A
/// tenant writes its own rules; the kernel ships none.
/// </summary>
public sealed class ConflictRule : AuditedEntity, ITenantWide, ITrackedChanges
{
    /// <summary>The record type of a rule's events and history (TF-062).</summary>
    public const string RecordType = "conflict_rule";

    public const int NameMaxLength = 200;
    public const int ValueMaxLength = 100;
    public const int ReasonMaxLength = 1000;

    private ConflictRule()
    {
        Name = string.Empty;
        FirstKind = string.Empty;
        FirstValue = string.Empty;
        SecondKind = string.Empty;
        SecondValue = string.Empty;
        Mode = string.Empty;
        Reason = string.Empty;
    }

    public ConflictRule(string name, string firstKind, string firstValue, string secondKind, string secondValue, string mode, string reason, Guid ownerId)
        : this()
    {
        Change(name, firstKind, firstValue, secondKind, secondValue, mode, reason, ownerId);
    }

    public string Name { get; private set; }

    public string FirstKind { get; private set; }

    /// <summary>A permission's name, or a role's id.</summary>
    public string FirstValue { get; private set; }

    public string SecondKind { get; private set; }

    public string SecondValue { get; private set; }

    public string Mode { get; private set; }

    /// <summary>Why the two may not go together, in the tenant's words: what a refusal and the report say.</summary>
    public string Reason { get; private set; }

    /// <summary>Who answers for the rule.</summary>
    public Guid OwnerId { get; private set; }

    public void Change(string name, string firstKind, string firstValue, string secondKind, string secondValue, string mode, string reason, Guid ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondValue);
        if (!ConflictSides.IsKnown(firstKind) || !ConflictSides.IsKnown(secondKind))
        {
            throw new ArgumentException("A side is a permission or a role.");
        }

        if (!ConflictModes.IsKnown(mode))
        {
            throw new ArgumentException("A rule blocks or warns.", nameof(mode));
        }

        Name = name.Trim();
        FirstKind = firstKind;
        FirstValue = firstValue;
        SecondKind = secondKind;
        SecondValue = secondValue;
        Mode = mode;
        Reason = (reason ?? string.Empty).Trim();
        OwnerId = ownerId;
    }
}

/// <summary>What started a report run: the worker's daily pass, an administrator, or a change of one person's rights.</summary>
public static class ConflictReportTriggers
{
    public const string Daily = "daily";
    public const string OnDemand = "on_demand";
}

/// <summary>One full run of the conflicts report (TF-036 AC3): when, why, and what it found.</summary>
public sealed class ConflictReportRun : AppendOnlyEntity, ITenantWide
{
    private ConflictReportRun()
    {
        Trigger = string.Empty;
    }

    public ConflictReportRun(string trigger, int rules, int conflicts)
        : this()
    {
        Trigger = trigger;
        Rules = rules;
        Conflicts = conflicts;
    }

    public string Trigger { get; private set; }

    public int Rules { get; private set; }

    public int Conflicts { get; private set; }
}

/// <summary>Where a conflicting right comes from: a role assignment of the person, or a delegation to them.</summary>
public static class ConflictSources
{
    public const string Assignment = "assignment";
    public const string Delegation = "delegation";
}

/// <summary>
/// One conflict of the report (TF-036): a person, the rule, the two grants that cause it, and the unit both reach (none
/// for the whole tenant). A full run replaces every entry; a change of one person's rights replaces theirs, so a warned
/// grant is listed at once (AC2).
/// </summary>
public sealed class ConflictReportEntry : AuditedEntity, ITenantWide
{
    private ConflictReportEntry()
    {
        RuleName = string.Empty;
        Mode = string.Empty;
        PrincipalName = string.Empty;
        FirstRole = string.Empty;
        FirstScopeKind = string.Empty;
        FirstSource = string.Empty;
        SecondRole = string.Empty;
        SecondScopeKind = string.Empty;
        SecondSource = string.Empty;
    }

    public ConflictReportEntry(ConflictRule rule, Guid principalId, string principalName, ConflictGrantFacts first, ConflictGrantFacts second, Guid? unitId, string? unitName, DateTimeOffset computedAt)
        : this()
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        RuleId = rule.Id;
        RuleName = rule.Name;
        Mode = rule.Mode;
        PrincipalId = principalId;
        PrincipalName = principalName;
        FirstAssignmentId = first.AssignmentId;
        FirstDelegationId = first.DelegationId;
        FirstRole = first.Role;
        FirstScopeKind = first.ScopeKind;
        FirstUnitId = first.UnitId;
        FirstUnitName = first.UnitName;
        FirstSource = first.Source;
        SecondAssignmentId = second.AssignmentId;
        SecondDelegationId = second.DelegationId;
        SecondRole = second.Role;
        SecondScopeKind = second.ScopeKind;
        SecondUnitId = second.UnitId;
        SecondUnitName = second.UnitName;
        SecondSource = second.Source;
        UnitId = unitId;
        UnitName = unitName;
        ComputedAt = computedAt;
    }

    public Guid RuleId { get; private set; }

    public string RuleName { get; private set; }

    public string Mode { get; private set; }

    public Guid PrincipalId { get; private set; }

    public string PrincipalName { get; private set; }

    public Guid? FirstAssignmentId { get; private set; }

    public Guid? FirstDelegationId { get; private set; }

    public string FirstRole { get; private set; }

    public string FirstScopeKind { get; private set; }

    public Guid? FirstUnitId { get; private set; }

    public string? FirstUnitName { get; private set; }

    public string FirstSource { get; private set; }

    public Guid? SecondAssignmentId { get; private set; }

    public Guid? SecondDelegationId { get; private set; }

    public string SecondRole { get; private set; }

    public string SecondScopeKind { get; private set; }

    public Guid? SecondUnitId { get; private set; }

    public string? SecondUnitName { get; private set; }

    public string SecondSource { get; private set; }

    /// <summary>The unit both grants reach, the deeper of the two; none when one of them is tenant-wide over a record or the tenant.</summary>
    public Guid? UnitId { get; private set; }

    public string? UnitName { get; private set; }

    public DateTimeOffset ComputedAt { get; private set; }
}

/// <summary>One grant of a conflict as the report keeps it: where it comes from, the role, and where it applies.</summary>
public sealed record ConflictGrantFacts(Guid? AssignmentId, Guid? DelegationId, string Role, string ScopeKind, Guid? UnitId, string? UnitName, string Source);
