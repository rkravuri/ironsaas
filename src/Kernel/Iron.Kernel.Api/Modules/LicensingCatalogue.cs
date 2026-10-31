using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iron.Kernel.Api.Modules;

/// <summary>A licensed module as the catalogue lists it: its licence code, the module that declares it, how it is sold, and its features.</summary>
public sealed record LicensedModuleEntry(string Code, string Module, SubscriptionType SubscriptionType, IReadOnlyList<string> Features)
{
    /// <summary>The suite of the declaring module (ADR-0043); the file says it once, as the group the entry sits in.</summary>
    [JsonIgnore]
    public string Suite { get; init; } = SuiteName.Foundation;
}

/// <summary>A feature as the catalogue lists it: its code, the module that declares it, that module's licence code (null for a kernel module), and what it is.</summary>
public sealed record FeatureEntry(string Code, string Module, string? Licence, string Description)
{
    /// <summary>The suite of the declaring module (ADR-0043); the file says it once, as the group the entry sits in.</summary>
    [JsonIgnore]
    public string Suite { get; init; } = SuiteName.Foundation;
}

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

    /// <summary>The suite of the declaring module (ADR-0043): <c>foundation</c> for the kill switches.</summary>
    [JsonIgnore]
    public string Suite { get; init; } = SuiteName.Foundation;

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
    /// <summary>The suite of the declaring module (ADR-0043); the file says it once, as the group the entry sits in.</summary>
    [JsonIgnore]
    public string Suite { get; init; } = SuiteName.Foundation;

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
/// A plan a suite brings to start from (TF-244, ADR-0043 decision 8, ADR-0059): its code, name, grace days and the codes
/// of its modules and features, every one of them the suite's. The control plane seeds it once; then it is the
/// operator's. The suite declares it beside its modules, so the Foundation names no suite's plan.
/// </summary>
public sealed record StarterPlanEntry(string Code, string Name, int GraceDays, IReadOnlyList<string> Modules, IReadOnlyList<string> Features)
{
    /// <summary>The suite that brings it; the file says it once, as the group the entry sits in.</summary>
    [JsonIgnore]
    public string Suite { get; init; } = SuiteName.Foundation;
}

/// <summary>
/// A domain event as the catalogue lists it (TF-110, ADR-0060): its name and version, the module that publishes it, the
/// record type it is about, what it means and who owns it, the fields of its data, the day it stops being published when
/// a newer version replaces it, and the subscriptions of this build that read it.
/// </summary>
public sealed record EventEntry(
    string Name,
    int Version,
    string Module,
    string RecordType,
    string Description,
    string Owner,
    IReadOnlyList<EventField> Fields,
    DateOnly? DeprecatedOn,
    int? ReplacedBy,
    IReadOnlyList<string> Subscribers)
{
    /// <summary>The suite of the publishing module (ADR-0043); the file says it once, as the group the entry sits in.</summary>
    [JsonIgnore]
    public string Suite { get; init; } = SuiteName.Foundation;

    public static EventEntry From(EventType type, IEnumerable<EventSubscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(subscriptions);
        var readers = subscriptions.Where(s => s.Event.Name == type.Name && s.Event.Version == type.Version).Select(s => s.Name).Order(StringComparer.Ordinal);
        return new EventEntry(type.Name, type.Version, type.Module, type.RecordType, type.Description, type.Owner, type.Fields, type.DeprecatedOn, type.ReplacedBy, [.. readers]);
    }
}

/// <summary>A duty as the catalogue lists it (ADR-0037): its code, the module that declares it, its name, what it is, and the permissions it covers.</summary>
public sealed record DutyEntry(string Code, string Module, string Name, string Description, IReadOnlyList<string> Permissions)
{
    public static DutyEntry From(Duty duty, string module)
    {
        ArgumentNullException.ThrowIfNull(duty);
        return new DutyEntry(duty.Code, module, duty.Name, duty.Description, [.. duty.Permissions.Select(p => p.Name).Order(StringComparer.Ordinal)]);
    }
}

/// <summary>
/// What a build can license and switch (ADR-0034, ADR-0035), and the settings (ADR-0036) and duties (ADR-0037) it declares: the licensed modules, the features and the flags its
/// modules declare in code, with the kernel's kill switches. AddModules builds it from the host's modules; the control
/// plane reads the same catalogue from the file the unit host writes (<c>make catalogue</c>), so a plan or a flag rule is
/// checked against the build the units run, without the control plane loading Business or suite modules.
/// The file groups the modules, features, flags and settings by suite, the Foundation first (ADR-0043); in memory the
/// lists are flat, by code, and each entry knows its suite.
/// </summary>
public sealed class LicensingCatalogue
{
    /// <summary>Version 2 groups by suite (ADR-0043); version 1 was one flat list of each.</summary>
    public const int FormatVersion = 2;

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

    public LicensingCatalogue(IEnumerable<LicensedModuleEntry> modules, IEnumerable<FeatureEntry> features, IEnumerable<string>? platform = null, IEnumerable<FlagEntry>? flags = null, IEnumerable<SettingEntry>? settings = null, IEnumerable<DutyEntry>? duties = null, IEnumerable<StarterPlanEntry>? plans = null, IEnumerable<EventEntry>? events = null)
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
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var duty in duties ?? [])
        {
            if (!codes.Add(duty.Code))
            {
                throw new InvalidOperationException($"The duty '{duty.Code}' is declared twice.");
            }
        }

        Duties = [.. (duties ?? []).OrderBy(d => d.Code, StringComparer.Ordinal)];
        var planCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in plans ?? [])
        {
            if (!planCodes.Add(plan.Code))
            {
                throw new InvalidOperationException($"The starter plan '{plan.Code}' is declared twice.");
            }
        }

        Plans = [.. (plans ?? []).OrderBy(p => p.Code, StringComparer.Ordinal)];
        var eventKeys = new HashSet<(string, int)>();
        foreach (var entry in events ?? [])
        {
            if (!eventKeys.Add((entry.Name, entry.Version)))
            {
                throw new InvalidOperationException($"The event '{entry.Name}' version {entry.Version} is declared twice.");
            }
        }

        Events = [.. (events ?? []).OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Version)];
        Suites = [.. Modules.Select(m => m.Suite)
            .Concat(Features.Select(f => f.Suite))
            .Concat(Flags.Select(f => f.Suite))
            .Concat(Settings.Select(s => s.Suite))
            .Concat(Plans.Select(p => p.Suite))
            .Concat(Events.Select(e => e.Suite))
            .Where(suite => suite != SuiteName.Foundation)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Prepend(SuiteName.Foundation)];
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

    /// <summary>Duties by code, the kernel's <c>all</c> among them.</summary>
    public IReadOnlyList<DutyEntry> Duties { get; }

    /// <summary>The plans the suites bring to start from (TF-244), by code; the Foundation's plans are the control plane's own.</summary>
    public IReadOnlyList<StarterPlanEntry> Plans { get; }

    /// <summary>The domain events the build's modules publish (TF-110), by name and version, each with its subscribers.</summary>
    public IReadOnlyList<EventEntry> Events { get; }

    /// <summary>The groups of the file: <c>foundation</c> first, then every suite with an entry, by short name (ADR-0043).</summary>
    public IReadOnlyList<string> Suites { get; }

    public EventEntry? FindEvent(string name, int version) => Events.FirstOrDefault(e => e.Name == name && e.Version == version);

    public LicensedModuleEntry? FindModule(string code) => _modules.GetValueOrDefault(code);

    public FeatureEntry? FindFeature(string code) => _features.GetValueOrDefault(code);

    /// <summary>A flag of this catalogue, retired or not.</summary>
    public FlagEntry? FindFlag(string key) => _flags.GetValueOrDefault(key);

    /// <summary>A flag the code declares now: a rule may name it and an evaluation may ask for it.</summary>
    public FlagEntry? FindActiveFlag(string key) => _flags.GetValueOrDefault(key) is { Status: FlagStatus.Active } flag ? flag : null;

    /// <summary>
    /// The catalogue of a host's modules, with the kernel's kill switches, in the order nothing depends on: by code; and
    /// the starter plans of <paramref name="suites"/>, which <c>make catalogue</c> passes for every suite the image carries.
    /// </summary>
    public static LicensingCatalogue From(IEnumerable<IModule> modules, IEnumerable<SuiteDefinition>? suites = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var plans = (suites ?? []).SelectMany(suite => suite.StarterPlans.Select(plan => plan with { Suite = suite.ShortName })).ToList();
        var licensed = new List<LicensedModuleEntry>();
        var features = new List<FeatureEntry>();
        var platform = new List<string>();
        var flags = KillSwitches.All.Select(flag => FlagEntry.From(flag, KernelModule)).ToList();
        var settings = new List<SettingEntry>();
        var duties = new List<DutyEntry> { DutyEntry.From(KernelDuties.All, KernelModule) };
        var list = modules.ToList();
        var subscriptions = list.SelectMany(m => m.Subscriptions).ToList();
        var events = new List<EventEntry>();
        foreach (var module in list)
        {
            var suite = module.Suite;
            if (suite != SuiteName.Foundation && !SuiteName.IsShortName(suite))
            {
                throw new InvalidOperationException($"The module '{module.Name}' names the suite '{suite}', which is not a suite's short name (ADR-0043).");
            }

            if (module.Licence is { } licence)
            {
                licensed.Add(new LicensedModuleEntry(licence.Code, module.Name, licence.SubscriptionType, [.. module.Features.Select(f => f.Code).Order(StringComparer.Ordinal)]) { Suite = suite });
            }
            else if (suite == SuiteName.Foundation)
            {
                platform.Add(module.Name);
            }
            else
            {
                throw new InvalidOperationException($"The module '{module.Name}' of the suite '{suite}' declares no licence; a suite's modules are licensed, only the kernel is the platform (ADR-0034, ADR-0043).");
            }

            features.AddRange(module.Features.Select(feature => new FeatureEntry(feature.Code, module.Name, module.Licence?.Code, feature.Description) { Suite = suite }));
            foreach (var flag in module.Flags)
            {
                if (flag.IsKillSwitch)
                {
                    throw new InvalidOperationException($"The module '{module.Name}' declares '{flag.Key}', but the kill-switch family is the kernel's (KillSwitches).");
                }

                flags.Add(FlagEntry.From(flag, module.Name) with { Suite = suite });
            }

            settings.AddRange(module.Settings.Select(setting => SettingEntry.From(setting, module.Name) with { Suite = suite }));
            duties.AddRange(module.Duties.Select(duty => DutyEntry.From(duty, module.Name)));
            events.AddRange(module.Events.Select(type => EventEntry.From(type, subscriptions) with { Suite = suite }));
        }

        // A starter plan names only its own suite's modules and features (TF-244 AC7): refused here, at make catalogue.
        foreach (var plan in plans)
        {
            var stray = plan.Modules.Where(code => !licensed.Any(m => m.Code == code && m.Suite == plan.Suite))
                .Concat(plan.Features.Where(code => !features.Any(f => f.Code == code && f.Suite == plan.Suite)))
                .ToList();
            if (stray.Count > 0)
            {
                throw new InvalidOperationException($"The starter plan '{plan.Code}' of the suite '{plan.Suite}' names {string.Join(", ", stray)}, which is no module or feature of that suite (TF-244).");
            }
        }

        return new LicensingCatalogue(licensed, features, platform, flags, settings, duties, plans, events);
    }

    /// <summary>
    /// This catalogue (made from the code) with the retired flags of the file kept, so a flag's retirement stays on
    /// record. Call it only once <see cref="FlagLifecycleProblems"/> is empty.
    /// </summary>
    public LicensingCatalogue WithRetiredFrom(LicensingCatalogue file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var retired = file.Flags.Where(f => f.Status == FlagStatus.Retired && FindFlag(f.Key) is null);
        return new LicensingCatalogue(Modules, Features, Platform, Flags.Concat(retired), Settings, Duties, Plans, Events);
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

    /// <summary>
    /// The file form: indented JSON with a format version, ending in a newline, so a diff shows what a build adds. The
    /// licensed modules, features, flags and settings sit in one group per suite, the Foundation's first (ADR-0043).
    /// </summary>
    public string ToJson()
    {
        var groups = Suites.Select(suite => new SuiteGroup(
            suite,
            [.. Modules.Where(m => m.Suite == suite)],
            [.. Features.Where(f => f.Suite == suite)],
            [.. Flags.Where(f => f.Suite == suite)],
            [.. Settings.Where(s => s.Suite == suite)],
            [.. Plans.Where(p => p.Suite == suite)],
            [.. Events.Where(e => e.Suite == suite)]));
        return JsonSerializer.Serialize(new CatalogueDocument(FormatVersion, Platform, [.. groups], Duties), Json) + "\n";
    }

    public static LicensingCatalogue Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<CatalogueDocument>(json, Json) ?? throw new InvalidOperationException("The licensing catalogue is empty.");
        if (document.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException($"The licensing catalogue has format version {document.FormatVersion}; this build reads version {FormatVersion}.");
        }

        var groups = document.Suites ?? [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group.Suite != SuiteName.Foundation && !SuiteName.IsShortName(group.Suite))
            {
                throw new InvalidOperationException($"The licensing catalogue has a group '{group.Suite}', which is neither the Foundation nor a suite's short name.");
            }

            if (!names.Add(group.Suite))
            {
                throw new InvalidOperationException($"The licensing catalogue has the group '{group.Suite}' twice.");
            }
        }

        return new LicensingCatalogue(
            groups.SelectMany(g => (g.Modules ?? []).Select(m => m with { Suite = g.Suite })),
            groups.SelectMany(g => (g.Features ?? []).Select(f => f with { Suite = g.Suite })),
            document.Platform,
            groups.SelectMany(g => (g.Flags ?? []).Select(f => f with { Suite = g.Suite })),
            groups.SelectMany(g => (g.Settings ?? []).Select(s => s with { Suite = g.Suite })),
            document.Duties,
            groups.SelectMany(g => (g.Plans ?? []).Select(p => p with { Suite = g.Suite })),
            groups.SelectMany(g => (g.Events ?? []).Select(e => e with { Suite = g.Suite })));
    }

    private sealed record CatalogueDocument(int FormatVersion, IReadOnlyList<string>? Platform, IReadOnlyList<SuiteGroup>? Suites, IReadOnlyList<DutyEntry>? Duties);

    /// <summary>One suite's part of the file, or the Foundation's: what it licenses, switches and sets, the plans it brings (TF-244), and the events it publishes (TF-110).</summary>
    private sealed record SuiteGroup(string Suite, IReadOnlyList<LicensedModuleEntry>? Modules, IReadOnlyList<FeatureEntry>? Features, IReadOnlyList<FlagEntry>? Flags, IReadOnlyList<SettingEntry>? Settings, IReadOnlyList<StarterPlanEntry>? Plans = null, IReadOnlyList<EventEntry>? Events = null);
}
