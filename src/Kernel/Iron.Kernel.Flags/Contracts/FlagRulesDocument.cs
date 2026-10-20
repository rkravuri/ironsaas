using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iron.Kernel.Flags.Contracts;

/// <summary>
/// Where a rule applies (TF-050, ADR-0035), from the most specific: one user, an organisation unit (the principal's home
/// unit or one above it), a tenant, a region, everyone. The most specific scope that has a matching rule wins.
/// </summary>
public enum FlagScope
{
    Global,
    Region,
    Tenant,
    Unit,
    User,
}

/// <summary>What else a rule asks of the subject: nothing, a share of a stable hash, or an attribute.</summary>
public enum FlagConditionKind
{
    Always,
    Percentage,
    Attribute,
}

/// <summary>Whose id a percentage rollout hashes: the tenant's (TF-050 AC1) or the person's.</summary>
public enum RolloutSubject
{
    Tenant,
    User,
}

/// <summary>A rule's condition: a percentage of tenants or users by a stable hash, or an attribute among values.</summary>
public sealed record FlagCondition(FlagConditionKind Kind, int? Percentage, RolloutSubject? Of, string? Attribute, IReadOnlyList<string>? Values)
{
    public static readonly FlagCondition Always = new(FlagConditionKind.Always, null, null, null, null);

    public static FlagCondition PercentageOf(int percentage, RolloutSubject of) => new(FlagConditionKind.Percentage, percentage, of, null, null);

    public static FlagCondition AttributeIn(string attribute, params string[] values) => new(FlagConditionKind.Attribute, null, null, attribute, values);
}

/// <summary>
/// One rule as the control plane publishes it: the flag, the scope and its target, the condition, the value, its place
/// among the rules of the same scope, and the operator's reason and time (a kill switch's banner shows both).
/// </summary>
public sealed record FlagRuleEntry(
    Guid Id,
    string Flag,
    FlagScope Scope,
    string? Region,
    string? Tenant,
    Guid? UnitId,
    Guid? UserId,
    FlagCondition? Condition,
    JsonElement Value,
    int Position,
    string? Reason,
    DateTimeOffset ChangedAt);

/// <summary>The attributes a rule may match (TF-050). The list grows with the evaluation context; the control plane refuses others.</summary>
public static class FlagAttributes
{
    public const string PrincipalType = "principal_type";
    public const string Plan = "plan";
    public const string Region = "region";

    public static IReadOnlyList<string> All => [PrincipalType, Plan, Region];
}

/// <summary>
/// The rules of one store path (ADR-0035): <c>platform/flags</c> holds the global and region rules of every flag, one write
/// for the whole platform; <c>tenants/&lt;slug&gt;/flags</c> holds a tenant's tenant, unit and user rules, with the tenant's
/// region and plan for attribute rules. The revision grows by one with every change of the path.
/// </summary>
public sealed record FlagRulesDocument(
    int FormatVersion,
    string Path,
    int Revision,
    DateTimeOffset PublishedAt,
    IReadOnlyDictionary<string, string>? Attributes,
    IReadOnlyList<FlagRuleEntry> Rules)
{
    public const int CurrentFormat = 1;

    public const string PlatformPath = "platform/flags";

    public const string SecretName = "flags";

    public const string DocumentKey = "document";

    public const string RevisionKey = "revision";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>A tenant's path, relative to the mount.</summary>
    public static string TenantPath(string tenant) => $"tenants/{tenant}/{SecretName}";

    public static FlagRulesDocument Empty(string path, DateTimeOffset at) => new(CurrentFormat, path, 0, at, null, []);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a document; a format this build does not know is refused, so a unit never guesses.</summary>
    public static FlagRulesDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<FlagRulesDocument>(json, Json) ?? throw new FormatException("The flag rules document is empty.");
        if (document.FormatVersion != CurrentFormat)
        {
            throw new FormatException($"The flag rules document has format version {document.FormatVersion}; this build reads version {CurrentFormat}.");
        }

        return document with { Rules = document.Rules ?? [] };
    }

    /// <summary>The store's key-value form of the document.</summary>
    public IReadOnlyDictionary<string, string> ToSecret() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DocumentKey] = ToJson(),
        [RevisionKey] = Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Serialises a value the way documents carry it, for the control plane.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
}
