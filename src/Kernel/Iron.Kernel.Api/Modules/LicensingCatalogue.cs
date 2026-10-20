using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iron.Kernel.Api.Modules;

/// <summary>A licensed module as the catalogue lists it: its licence code, the module that declares it, how it is sold, and its features.</summary>
public sealed record LicensedModuleEntry(string Code, string Module, SubscriptionType SubscriptionType, IReadOnlyList<string> Features);

/// <summary>A feature as the catalogue lists it: its code, the module that declares it, that module's licence code (null for a kernel module), and what it is.</summary>
public sealed record FeatureEntry(string Code, string Module, string? Licence, string Description);

/// <summary>
/// What a build can license (ADR-0034): the licensed modules and the features its modules declare in code. AddModules
/// builds it from the host's modules; the control plane reads the same catalogue from the file the unit host writes
/// (<c>make catalogue</c>), so a plan is checked against the build the units run, without the control plane loading
/// Business or Products modules.
/// </summary>
public sealed class LicensingCatalogue
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        // The file is read by people in a diff and by the control plane, never put into a page: no HTML escaping.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly Dictionary<string, LicensedModuleEntry> _modules;
    private readonly Dictionary<string, FeatureEntry> _features;

    public LicensingCatalogue(IEnumerable<LicensedModuleEntry> modules, IEnumerable<FeatureEntry> features, IEnumerable<string>? platform = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(features);
        Platform = [.. (platform ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        _modules = new Dictionary<string, LicensedModuleEntry>(StringComparer.Ordinal);
        foreach (var module in modules.OrderBy(m => m.Code, StringComparer.Ordinal))
        {
            if (!_modules.TryAdd(module.Code, module))
            {
                throw new InvalidOperationException($"The licence code '{module.Code}' is declared twice.");
            }
        }

        _features = new Dictionary<string, FeatureEntry>(StringComparer.Ordinal);
        foreach (var feature in features.OrderBy(f => f.Code, StringComparer.Ordinal))
        {
            if (!_features.TryAdd(feature.Code, feature))
            {
                throw new InvalidOperationException($"The feature '{feature.Code}' is declared twice.");
            }
        }

        Modules = [.. _modules.Values];
        Features = [.. _features.Values];
    }

    public static LicensingCatalogue Empty { get; } = new([], []);

    /// <summary>The names of the modules that are the platform: always on, never in a plan (ADR-0034).</summary>
    public IReadOnlyList<string> Platform { get; }

    /// <summary>Licensed modules by code.</summary>
    public IReadOnlyList<LicensedModuleEntry> Modules { get; }

    /// <summary>Features by code.</summary>
    public IReadOnlyList<FeatureEntry> Features { get; }

    public LicensedModuleEntry? FindModule(string code) => _modules.GetValueOrDefault(code);

    public FeatureEntry? FindFeature(string code) => _features.GetValueOrDefault(code);

    /// <summary>The catalogue of a host's modules, in the order nothing depends on: by code.</summary>
    public static LicensingCatalogue From(IEnumerable<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var licensed = new List<LicensedModuleEntry>();
        var features = new List<FeatureEntry>();
        var platform = new List<string>();
        foreach (var module in modules)
        {
            if (module.Licence is { } licence)
            {
                licensed.Add(new LicensedModuleEntry(licence.Code, module.Name, licence.SubscriptionType, [.. module.Features.Select(f => f.Code).Order(StringComparer.Ordinal)]));
            }
            else
            {
                platform.Add(module.Name);
            }

            features.AddRange(module.Features.Select(feature => new FeatureEntry(feature.Code, module.Name, module.Licence?.Code, feature.Description)));
        }

        return new LicensingCatalogue(licensed, features, platform);
    }

    /// <summary>The file form: indented JSON with a format version, ending in a newline, so a diff shows what a build adds.</summary>
    public string ToJson() => JsonSerializer.Serialize(new CatalogueDocument(FormatVersion, Platform, Modules, Features), Json) + "\n";

    public static LicensingCatalogue Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<CatalogueDocument>(json, Json) ?? throw new InvalidOperationException("The licensing catalogue is empty.");
        if (document.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException($"The licensing catalogue has format version {document.FormatVersion}; this build reads version {FormatVersion}.");
        }

        return new LicensingCatalogue(document.Modules ?? [], document.Features ?? [], document.Platform);
    }

    private sealed record CatalogueDocument(int FormatVersion, IReadOnlyList<string>? Platform, IReadOnlyList<LicensedModuleEntry>? Modules, IReadOnlyList<FeatureEntry>? Features);
}
