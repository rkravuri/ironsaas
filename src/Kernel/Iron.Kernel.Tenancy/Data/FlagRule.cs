using System.Text.Json;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Flags.Contracts;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>
/// One rule of a flag (TF-050, ADR-0035): its scope and target (a region, a tenant, a unit or a user of a tenant), an
/// optional condition (a percentage by stable hash, or an attribute among values), the value, its place among the
/// flag's rules, and for a kill switch the operator's reason. A flag's rules are replaced together, in one transaction.
/// </summary>
public sealed class FlagRule : AuditedEntity, ITenantWide
{
    public const int ReasonMaxLength = 500;

    private FlagRule()
    {
        FlagKey = string.Empty;
        ValueJson = "null";
        AttributeValues = [];
    }

    public FlagRule(string flagKey, FlagRuleEntry entry, int position)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagKey);
        ArgumentNullException.ThrowIfNull(entry);
        FlagKey = flagKey;
        Scope = entry.Scope;
        Region = entry.Region;
        TenantSlug = entry.Tenant;
        UnitId = entry.UnitId;
        UserId = entry.UserId;
        ConditionKind = entry.Condition?.Kind ?? FlagConditionKind.Always;
        Percentage = entry.Condition?.Percentage;
        RolloutOf = entry.Condition?.Of;
        Attribute = entry.Condition?.Attribute;
        AttributeValues = [.. entry.Condition?.Values ?? []];
        ValueJson = entry.Value.GetRawText();
        Position = position;
        Reason = entry.Reason?.Trim() is { Length: > 0 } reason ? reason[..Math.Min(reason.Length, ReasonMaxLength)] : null;
    }

    public string FlagKey { get; private set; }

    public FlagScope Scope { get; private set; }

    public string? Region { get; private set; }

    /// <summary>The tenant of a tenant, unit or user rule, by slug: the store path it is published at.</summary>
    public string? TenantSlug { get; private set; }

    public Guid? UnitId { get; private set; }

    public Guid? UserId { get; private set; }

    public FlagConditionKind ConditionKind { get; private set; }

    public int? Percentage { get; private set; }

    public RolloutSubject? RolloutOf { get; private set; }

    public string? Attribute { get; private set; }

    public List<string> AttributeValues { get; private set; }

    /// <summary>The value as JSON (jsonb), of the flag's type.</summary>
    public string ValueJson { get; private set; }

    public int Position { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>The store path this rule is published at: the platform's for a global or region rule, its tenant's otherwise.</summary>
    public string StorePath => Scope is FlagScope.Global or FlagScope.Region ? FlagRulesDocument.PlatformPath : FlagRulesDocument.TenantPath(TenantSlug!);

    public FlagCondition Condition => ConditionKind switch
    {
        FlagConditionKind.Percentage => new FlagCondition(ConditionKind, Percentage, RolloutOf, null, null),
        FlagConditionKind.Attribute => new FlagCondition(ConditionKind, null, null, Attribute, [.. AttributeValues]),
        _ => FlagCondition.Always,
    };

    /// <summary>
    /// The rule as the store's document carries it, with when it was set: a rule is never changed in place, only moved,
    /// so its creation is when its value began to apply.
    /// </summary>
    public FlagRuleEntry ToEntry() =>
        new(Id, FlagKey, Scope, Region, TenantSlug, UnitId, UserId, Condition, JsonDocument.Parse(ValueJson).RootElement.Clone(), Position, Reason, CreatedAt);

    /// <summary>The same rule: scope, target, condition, value and reason; the place among the rules aside.</summary>
    public bool SameAs(FlagRule other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Scope == other.Scope
            && Region == other.Region
            && TenantSlug == other.TenantSlug
            && UnitId == other.UnitId
            && UserId == other.UserId
            && ConditionKind == other.ConditionKind
            && Percentage == other.Percentage
            && RolloutOf == other.RolloutOf
            && Attribute == other.Attribute
            && AttributeValues.SequenceEqual(other.AttributeValues, StringComparer.Ordinal)
            && Reason == other.Reason
            && JsonElement.DeepEquals(JsonDocument.Parse(ValueJson).RootElement, JsonDocument.Parse(other.ValueJson).RootElement);
    }

    public void MoveTo(int position) => Position = position;
}
