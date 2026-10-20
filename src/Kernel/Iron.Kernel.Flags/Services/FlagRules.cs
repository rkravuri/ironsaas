using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Flags.Contracts;

namespace Iron.Kernel.Flags.Services;

/// <summary>
/// Who a flag is evaluated for (ADR-0035): the tenant, its region and plan, the principal and its type, and the units
/// from the principal's home unit up to the root, nearest first. Anything left out matches no rule that needs it.
/// </summary>
public sealed record FlagSubject(string Tenant, string? Region, string? Plan, Guid? PrincipalId, string? PrincipalType, IReadOnlyList<Guid> Units)
{
    public string? Attribute(string name) => name switch
    {
        FlagAttributes.Region => Region,
        FlagAttributes.Plan => Plan,
        FlagAttributes.PrincipalType => PrincipalType,
        _ => null,
    };
}

/// <summary>What a flag evaluates to, why, and the rule that decided it (none for the default).</summary>
public sealed record FlagResult(JsonElement Value, string Reason, FlagRuleEntry? Rule)
{
    public const string DefaultReason = "default";
    public const string TargetingReason = "targeting_match";
    public const string SplitReason = "split";
    public const string UnknownReason = "unknown";
}

/// <summary>
/// The stable hash of a percentage rollout (TF-050 AC1): SHA-256 of the flag key and the subject's id, its first four
/// bytes read big-endian, in 10,000 buckets. The same tenant or person always lands in the same bucket of a flag, on
/// every node and in every release, and a flag's buckets do not follow another flag's.
/// </summary>
public static class StableHash
{
    public const int Buckets = 10_000;

    public static int Bucket(string flagKey, string subjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{flagKey}:{subjectId}"), hash);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % Buckets);
    }

    /// <summary>Whether the subject is in the first <paramref name="percentage"/> per cent of the flag's buckets.</summary>
    public static bool InRollout(string flagKey, string subjectId, int percentage) =>
        Bucket(flagKey, subjectId) < Math.Clamp(percentage, 0, 100) * (Buckets / 100);
}

/// <summary>
/// The evaluation order (ADR-0035), pure so it is tested on rules alone. The most specific scope with a matching rule
/// wins: user, then unit (the home unit before the units above it), then tenant, region, global. Within a scope the rules
/// are tried in their order, and the first whose target and condition match decides. No match: the declaration's default.
/// A rule whose value is not of the flag's type is skipped; the control plane refuses them, this is the second guard.
/// A kill switch is the exception: it is on when any rule that applies says so, at any scope (see <see cref="KillSwitch"/>).
/// </summary>
public static class FlagRules
{
    private static readonly FlagScope[] Precedence = [FlagScope.User, FlagScope.Unit, FlagScope.Tenant, FlagScope.Region, FlagScope.Global];

    public static FlagResult Evaluate(Flag flag, IEnumerable<FlagRuleEntry> rules, FlagSubject subject)
    {
        ArgumentNullException.ThrowIfNull(flag);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(subject);
        var mine = rules.Where(r => string.Equals(r.Flag, flag.Key, StringComparison.Ordinal) && Flag.Fits(flag.Type, r.Value)).ToList();
        if (flag.IsKillSwitch)
        {
            return KillSwitch(flag, mine, subject);
        }

        foreach (var scope in Precedence)
        {
            var candidates = mine.Where(r => r.Scope == scope && Targets(r, subject));
            var ordered = scope == FlagScope.Unit
                ? candidates.OrderBy(r => Distance(r, subject)).ThenBy(r => r.Position)
                : candidates.OrderBy(r => r.Position);
            foreach (var rule in ordered)
            {
                if (Holds(flag.Key, rule.Condition, subject))
                {
                    return new FlagResult(rule.Value, rule.Condition?.Kind == FlagConditionKind.Percentage ? FlagResult.SplitReason : FlagResult.TargetingReason, rule);
                }
            }
        }

        return new FlagResult(flag.Default, FlagResult.DefaultReason, null);
    }

    /// <summary>
    /// A kill switch stops its path when any rule that applies to the subject says so, at any scope, whatever a more
    /// specific rule says (ADR-0035): a switch the operator turns on for the platform stops the path for every tenant,
    /// and no tenant, unit or user rule can exempt anyone. The control plane refuses a rule that says false, since
    /// removing the rule is how a switch goes off; one stored before that rule is ignored here. The most specific rule
    /// that says so is the one reported, with its reason and its time.
    /// </summary>
    private static FlagResult KillSwitch(Flag flag, List<FlagRuleEntry> rules, FlagSubject subject)
    {
        foreach (var scope in Precedence)
        {
            var on = rules
                .Where(r => r.Scope == scope && r.Value.ValueKind == JsonValueKind.True && Targets(r, subject))
                .OrderBy(r => scope == FlagScope.Unit ? Distance(r, subject) : 0)
                .ThenBy(r => r.Position)
                .FirstOrDefault(r => Holds(flag.Key, r.Condition, subject));
            if (on is not null)
            {
                return new FlagResult(on.Value, on.Condition?.Kind == FlagConditionKind.Percentage ? FlagResult.SplitReason : FlagResult.TargetingReason, on);
            }
        }

        return new FlagResult(flag.Default, FlagResult.DefaultReason, null);
    }

    /// <summary>Whether the rule's target is the subject's: its user, one of its units, its tenant, its region, or anyone.</summary>
    public static bool Targets(FlagRuleEntry rule, FlagSubject subject)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(subject);
        return rule.Scope switch
        {
            FlagScope.Global => true,
            FlagScope.Region => subject.Region is not null && string.Equals(rule.Region, subject.Region, StringComparison.Ordinal),
            FlagScope.Tenant => string.Equals(rule.Tenant, subject.Tenant, StringComparison.Ordinal),
            FlagScope.Unit => string.Equals(rule.Tenant, subject.Tenant, StringComparison.Ordinal) && rule.UnitId is { } unit && subject.Units.Contains(unit),
            FlagScope.User => string.Equals(rule.Tenant, subject.Tenant, StringComparison.Ordinal) && rule.UserId is { } user && user == subject.PrincipalId,
            _ => false,
        };
    }

    /// <summary>Whether the rule's condition holds for the subject. A percentage of users needs a principal.</summary>
    public static bool Holds(string flagKey, FlagCondition? condition, FlagSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (condition is null || condition.Kind == FlagConditionKind.Always)
        {
            return true;
        }

        if (condition.Kind == FlagConditionKind.Percentage)
        {
            var id = condition.Of == RolloutSubject.User ? subject.PrincipalId?.ToString("D") : subject.Tenant;
            return id is not null && StableHash.InRollout(flagKey, id, condition.Percentage ?? 0);
        }

        var value = condition.Attribute is null ? null : subject.Attribute(condition.Attribute);
        return value is not null && (condition.Values ?? []).Contains(value, StringComparer.Ordinal);
    }

    private static int Distance(FlagRuleEntry rule, FlagSubject subject)
    {
        for (var i = 0; i < subject.Units.Count; i++)
        {
            if (subject.Units[i] == rule.UnitId)
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
