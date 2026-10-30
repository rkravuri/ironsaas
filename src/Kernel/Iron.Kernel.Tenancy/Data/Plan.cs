using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>A licensed module in a plan (TF-042): its status, how it is sold (from the catalogue), and the seats for a per-person module.</summary>
public sealed record PlanModule(string Code, EntitlementStatus Status, SubscriptionType SubscriptionType, int? UserLimit);

/// <summary>
/// A template of entitlements (TF-042): licensed modules, features and a grace period in days, applied to a tenant in
/// one action. Kernel modules are never in a plan (ADR-0034); the codes are checked against the catalogue of the build
/// the units run when the plan is saved and again when it is applied. A plan belongs to one group, the Foundation or a
/// suite, and names only that group's modules and features (TF-244 AC7, ADR-0059); its group never changes.
/// </summary>
public sealed partial class Plan : AuditedEntity, ITenantWide
{
    public const int CodeMaxLength = 64;
    public const int NameMaxLength = 200;
    public const int SuiteMaxLength = 40;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private Plan()
    {
        Code = string.Empty;
        Name = string.Empty;
        Suite = SuiteName.Foundation;
        ModulesJson = "[]";
        Features = [];
    }

    public Plan(string code, string name, int graceDays, IReadOnlyList<PlanModule> modules, IReadOnlyList<string> features, string suite = SuiteName.Foundation)
        : this()
    {
        if (!IsValidCode(code))
        {
            throw new ArgumentException($"'{code}' is not a plan code.", nameof(code));
        }

        if (suite != SuiteName.Foundation && !SuiteName.IsShortName(suite))
        {
            throw new ArgumentException($"'{suite}' is neither the Foundation nor a suite's short name.", nameof(suite));
        }

        Code = code;
        Suite = suite;
        Change(name, graceDays, modules, features);
    }

    public string Code { get; private set; }

    /// <summary>The plan's group: <c>foundation</c>, which every tenant holds one plan of, or a suite's short name (TF-244).</summary>
    public string Suite { get; private set; }

    public string Name { get; private set; }

    /// <summary>Days after a subscription's end date in which its entitlements still work (TF-040 AC3).</summary>
    public int GraceDays { get; private set; }

    /// <summary>The licensed modules as JSON (jsonb); read them through <see cref="Modules"/>.</summary>
    public string ModulesJson { get; private set; }

    /// <summary>The features the plan includes, enabled (text[]).</summary>
    public List<string> Features { get; private set; }

    public IReadOnlyList<PlanModule> Modules => JsonSerializer.Deserialize<List<PlanModule>>(ModulesJson, Json) ?? [];

    public void Change(string name, int graceDays, IReadOnlyList<PlanModule> modules, IReadOnlyList<string> features)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentOutOfRangeException.ThrowIfNegative(graceDays);
        Name = name.Trim();
        GraceDays = graceDays;
        ModulesJson = JsonSerializer.Serialize(modules.OrderBy(m => m.Code, StringComparer.Ordinal).ToList(), Json);
        Features = [.. features.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>Lowercase letters, digits and hyphens, starting with a letter: <c>standard</c>, <c>enterprise</c>.</summary>
    public static bool IsValidCode(string? code) => code is not null && code.Length <= CodeMaxLength && CodePattern().IsMatch(code);

    [GeneratedRegex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
