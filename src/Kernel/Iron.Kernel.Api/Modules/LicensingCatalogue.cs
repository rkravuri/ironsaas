using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iron.Kernel.Api.Modules;

/// <summary>A licensed module as the catalogue lists it: its licence code, the module that declares it, how it is sold, and its features.</summary>
public sealed record LicensedModuleEntry(string Code, string Module, SubscriptionType SubscriptionType, IReadOnlyList<string> Features);

/// <summary>A feature as the catalogue lists it: its code, the module that declares it, that module's licence code (null for a kernel module), and what it is.</summary>
public sealed record FeatureEntry(string Code, string Module, string? Licence, string Description);

/// <summary>Whether a flag is in the code (active), or was taken out of it on purpose (retired, TF-052).</summary>
public enum FlagStatus
{
    Active,
    Retired,
}

/// <summary>
/// A flag as the catalogue lists it (ADR-0035): what its declaration says, the module that declares it (<c>kernel</c> for
/// the kill switches), and its status. A retired flag stays in the file as the record that it left the code on purpose.
/// </summary>
public sealed record FlagEntry(string Key, string Module, FlagType Type, FlagKind Kind, string Purpose, string Owner, DateOnly? RemoveBy, JsonElement Default, FlagStatus Status)
{
    [JsonIgnore]
    public bool IsKillSwitch => Key.StartsWith(Flag.KillSwitchPrefix, StringComparison.Ordinal);

    public static FlagEntry From(Flag flag, string module)
    {
        ArgumentNullException.ThrowIfNull(flag);
        return new FlagEntry(flag.Key, module, flag.Type, flag.Kind, flag.Purpose, flag.Owner, flag.RemoveBy, flag.Default, FlagStatus.Active);
    }
}

/// <summary>
/// A setting as the catalogue lists it (ADR-0036): what its declaration says, in the form the shell's typed keys are
/// written from. The rule is in words; a function rule cannot travel, its words do.
/// </summary>
public sealed record SettingEntry(
    string Key,
    string Module,
    SettingType Type,
    string Description,
    JsonElement Default,
    IReadOnlyList<SettingLevel> Scopes,
    bool VisibleToShell,
    string Requirement,
    IReadOnlyList<string>? Choices,
    decimal? Minimum,
    decimal? Maximum,
    bool WholeNumber)
{
    public static SettingEntry From(SettingDefinition setting, string module)
    {
        ArgumentNullException.ThrowIfNull(setting);
        IReadOnlyList<SettingLevel> scopes = [.. new[] { SettingLevel.Tenant, SettingLevel.Unit, SettingLevel.User }.Where(setting.Allows)];
        return new SettingEntry(
            setting.Key,
            module,
            setting.Type,
            setting.Description,
            setting.Default,
            scopes,
            setting.VisibleToShell,
            setting.Requirement,
            setting.Type == SettingType.Choice ? setting.Choices : null,
            setting.Rule?.Minimum,
            setting.Rule?.Maximum,
            setting.WholeNumber);
    }
}

/// <summary>
/// What a build can license and switch (ADR-0034, ADR-0035), and the settings it declares (ADR-0036): the licensed modules, the features and the flags its
/// modules declare in code, with the kernel's kill switches. AddModules builds it from the host's modules; the control
/// plane reads the same catalogue from the file the unit host writes (<c>make catalogue</c>), so a plan or a flag rule is
/// checked against the build the units run, without the control plane loading Business or Products modules.
/// </summary>
public sealed class LicensingCatalogue
{
    public const int FormatVersion = 1;

    /// <summary>The module the catalogue names for the kill switches, which every host has (ADR-0035).</summary>
    public const string KernelModule = "kernel";

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
    private readonly Dictionary<string, FlagEntry> _flags;

    public LicensingCatalogue(IEnumerable<LicensedModuleEntry> modules, IEnumerable<FeatureEntry> features, IEnumerable<string>? platform = null, IEnumerable<FlagEntry>? flags = null, IEnumerable<SettingEntry>? settings = null)
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

        _flags = new Dictionary<string, FlagEntry>(StringComparer.Ordinal);
        foreach (var flag in (flags ?? []).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!_flags.TryAdd(flag.Key, flag))
            {
                throw new InvalidOperationException($"The flag '{flag.Key}' is declared twice.");
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in settings ?? [])
        {
            if (!keys.Add(setting.Key))
            {
                throw new InvalidOperationException($"The setting '{setting.Key}' is declared twice.");
            }
        }

        Modules = [.. _modules.Values];
        Features = [.. _features.Values];
        Flags = [.. _flags.Values];
        Settings = [.. (settings ?? []).OrderBy(s => s.Key, StringComparer.Ordinal)];
    }

    public static LicensingCatalogue Empty { get; } = new([], []);

    /// <summary>The names of the modules that are the platform: always on, never in a plan (ADR-0034).</summary>
    public IReadOnlyList<string> Platform { get; }

    /// <summary>Licensed modules by code.</summary>
    public IReadOnlyList<LicensedModuleEntry> Modules { get; }

    /// <summary>Features by code.</summary>
    public IReadOnlyList<FeatureEntry> Features { get; }

    /// <summary>Flags by key, the retired ones included.</summary>
    public IReadOnlyList<FlagEntry> Flags { get; }

    /// <summary>Settings by key.</summary>
    public IReadOnlyList<SettingEntry> Settings { get; }

    public LicensedModuleEntry? FindModule(string code) => _modules.GetValueOrDefault(code);

    public FeatureEntry? FindFeature(string code) => _features.GetValueOrDefault(code);

    /// <summary>A flag of this catalogue, retired or not.</summary>
    public FlagEntry? FindFlag(string key) => _flags.GetValueOrDefault(key);

    /// <summary>A flag the code declares now: a rule may name it and an evaluation may ask for it.</summary>
    public FlagEntry? FindActiveFlag(string key) => _flags.GetValueOrDefault(key) is { Status: FlagStatus.Active } flag ? flag : null;

    /// <summary>The catalogue of a host's modules, with the kernel's kill switches, in the order nothing depends on: by code.</summary>
    public static LicensingCatalogue From(IEnumerable<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var licensed = new List<LicensedModuleEntry>();
        var features = new List<FeatureEntry>();
        var platform = new List<string>();
        var flags = KillSwitches.All.Select(flag => FlagEntry.From(flag, KernelModule)).ToList();
        var settings = new List<SettingEntry>();
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
            foreach (var flag in module.Flags)
            {
                if (flag.IsKillSwitch)
                {
                    throw new InvalidOperationException($"The module '{module.Name}' declares '{flag.Key}', but the kill-switch family is the kernel's (KillSwitches).");
                }

                flags.Add(FlagEntry.From(flag, module.Name));
            }

            settings.AddRange(module.Settings.Select(setting => SettingEntry.From(setting, module.Name)));
        }

        return new LicensingCatalogue(licensed, features, platform, flags, settings);
    }

    /// <summary>
    /// This catalogue (made from the code) with the retired flags of the file kept, so a flag's retirement stays on
    /// record. Call it only once <see cref="FlagLifecycleProblems"/> is empty.
    /// </summary>
    public LicensingCatalogue WithRetiredFrom(LicensingCatalogue file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var retired = file.Flags.Where(f => f.Status == FlagStatus.Retired && FindFlag(f.Key) is null);
        return new LicensingCatalogue(Modules, Features, Platform, Flags.Concat(retired), Settings);
    }

    /// <summary>
    /// TF-052 AC2, one message per flag: a flag the file marks active that the code no longer declares, and a flag the
    /// file marks retired that the code still declares. Retiring a flag is an explicit change of the file.
    /// </summary>
    public static IReadOnlyList<string> FlagLifecycleProblems(LicensingCatalogue file, LicensingCatalogue code)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(code);
        var problems = new List<string>();
        foreach (var flag in file.Flags)
        {
            var declared = code.FindFlag(flag.Key);
            if (flag.Status == FlagStatus.Active && declared is null)
            {
                problems.Add($"The flag '{flag.Key}' (owner: {flag.Owner}) left the code while the catalogue marks it active. Mark it \"status\": \"retired\" in the catalogue in the same change, or declare it again.");
            }
            else if (flag.Status == FlagStatus.Retired && declared is not null)
            {
                problems.Add($"The flag '{flag.Key}' is retired in the catalogue but the module '{declared.Module}' still declares it. Remove it from the code.");
            }
        }

        return problems;
    }

    /// <summary>The file form: indented JSON with a format version, ending in a newline, so a diff shows what a build adds.</summary>
    public string ToJson() => JsonSerializer.Serialize(new CatalogueDocument(FormatVersion, Platform, Modules, Features, Flags, Settings), Json) + "\n";

    public static LicensingCatalogue Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<CatalogueDocument>(json, Json) ?? throw new InvalidOperationException("The licensing catalogue is empty.");
        if (document.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException($"The licensing catalogue has format version {document.FormatVersion}; this build reads version {FormatVersion}.");
        }

        return new LicensingCatalogue(document.Modules ?? [], document.Features ?? [], document.Platform, document.Flags, document.Settings);
    }

    private sealed record CatalogueDocument(int FormatVersion, IReadOnlyList<string>? Platform, IReadOnlyList<LicensedModuleEntry>? Modules, IReadOnlyList<FeatureEntry>? Features, IReadOnlyList<FlagEntry>? Flags, IReadOnlyList<SettingEntry>? Settings);
}
