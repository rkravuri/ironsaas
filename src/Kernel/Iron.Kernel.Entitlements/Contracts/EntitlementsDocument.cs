using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iron.Kernel.Entitlements.Contracts;

/// <summary>One licensed module of a tenant as the control plane decided it: status, how it is sold, seats, and the subscription's dates.</summary>
public sealed record ModuleEntitlement(string Code, EntitlementStatus Status, SubscriptionType SubscriptionType, int? UserLimit, DateOnly StartsOn, DateOnly? EndsOn);

/// <summary>One feature of a tenant: status and the subscription's dates.</summary>
public sealed record FeatureEntitlement(string Code, EntitlementStatus Status, DateOnly StartsOn, DateOnly? EndsOn);

/// <summary>
/// A tenant's effective entitlements, as the control plane writes them to the store at <c>tenants/&lt;slug&gt;/entitlements</c>
/// after every committed change and the unit reads them (ADR-0034). Expired is not in it: the unit works it out from the
/// end date and the grace days with its own clock. The revision grows by one with every change.
/// </summary>
public sealed record EntitlementsDocument(
    int FormatVersion,
    string Tenant,
    int Revision,
    string? Plan,
    int GraceDays,
    DateTimeOffset PublishedAt,
    IReadOnlyList<ModuleEntitlement> Modules,
    IReadOnlyList<FeatureEntitlement> Features)
{
    public const int CurrentFormat = 1;

    /// <summary>The secret's name under the tenant's path, and the one key that holds the document.</summary>
    public const string SecretName = "entitlements";

    public const string DocumentKey = "document";

    public const string RevisionKey = "revision";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Nothing licensed: what a tenant without a plan has, and what the store's silence about a path means.</summary>
    public static EntitlementsDocument Empty(string tenant, DateTimeOffset at) => new(CurrentFormat, tenant, 0, null, 0, at, [], []);

    /// <summary>The store's path of a tenant's entitlements, relative to the mount.</summary>
    public static string PathOf(string tenant) => $"tenants/{tenant}/{SecretName}";

    public ModuleEntitlement? FindModule(string code) => Modules.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.Ordinal));

    public FeatureEntitlement? FindFeature(string code) => Features.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.Ordinal));

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a document; a format this build does not know is refused, so a unit never guesses.</summary>
    public static EntitlementsDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<EntitlementsDocument>(json, Json) ?? throw new FormatException("The entitlements document is empty.");
        if (document.FormatVersion != CurrentFormat)
        {
            throw new FormatException($"The entitlements document has format version {document.FormatVersion}; this build reads version {CurrentFormat}.");
        }

        return document with { Modules = document.Modules ?? [], Features = document.Features ?? [] };
    }

    /// <summary>The store's key-value form of the document.</summary>
    public IReadOnlyDictionary<string, string> ToSecret() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DocumentKey] = ToJson(),
        [RevisionKey] = Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
