using Microsoft.Extensions.Configuration;

namespace Iron.Kernel.Api.Modules;

/// <summary>
/// A suite as a host composes it (ADR-0043, ADR-0059): its short name, its name and a line on what it is for people,
/// its modules, made when a unit installs it, and the plans it brings to start from, which the catalogue carries to the
/// control plane. One image carries every suite; a unit loads only those its configuration names (TF-244).
/// </summary>
public sealed class SuiteDefinition
{
    private readonly Func<IReadOnlyList<IModule>> _modules;

    public SuiteDefinition(string shortName, string displayName, string description, Func<IReadOnlyList<IModule>> modules, IReadOnlyList<StarterPlanEntry>? starterPlans = null)
    {
        if (!SuiteName.IsShortName(shortName))
        {
            throw new ArgumentException($"'{shortName}' is not a suite's short name (ADR-0043).", nameof(shortName));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(modules);
        ShortName = shortName;
        DisplayName = displayName.Trim();
        Description = description.Trim();
        _modules = modules;
        StarterPlans = starterPlans ?? [];
    }

    /// <summary>The plans the suite brings to start from (TF-244): the control plane seeds them once, from the catalogue.</summary>
    public IReadOnlyList<StarterPlanEntry> StarterPlans { get; }

    /// <summary>The suite's short name: <c>procurement</c>.</summary>
    public string ShortName { get; }

    /// <summary>The suite's name for people: <c>eProcurement Suite</c>.</summary>
    public string DisplayName { get; }

    public string Description { get; }

    /// <summary>The suite's modules, new each time, in the order the suite lists them; a module of another suite stops the host.</summary>
    public IReadOnlyList<IModule> Modules()
    {
        var modules = _modules();
        var stray = modules.FirstOrDefault(module => !string.Equals(module.Suite, ShortName, StringComparison.Ordinal));
        if (stray is not null)
        {
            throw new InvalidOperationException($"The suite '{ShortName}' lists the module '{stray.Name}', which belongs to '{stray.Suite}' (ADR-0043).");
        }

        return modules;
    }
}

/// <summary>
/// The suites a unit runs (TF-244, ADR-0059): the short names in <c>Suites:Installed</c>, separated by commas, which the
/// fleet writes into the unit's environment from the control plane's record of the tenant's suites. Missing or empty:
/// the Foundation alone. A name this build does not carry stops the host rather than start a unit without it.
/// </summary>
public static class InstalledSuites
{
    public const string ConfigurationKey = "Suites:Installed";

    /// <summary>The suites of <paramref name="carried"/> the configuration names, in the order the host lists them.</summary>
    public static IReadOnlyList<SuiteDefinition> From(IConfiguration configuration, IReadOnlyList<SuiteDefinition> carried)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(carried);
        var named = Parse(configuration[ConfigurationKey]);
        var unknown = named.Where(name => carried.All(suite => suite.ShortName != name)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"The unit's configuration names the suite(s) {string.Join(", ", unknown)} in {ConfigurationKey}, which this build does not carry "
                + $"(it carries {(carried.Count == 0 ? "none" : string.Join(", ", carried.Select(s => s.ShortName)))}). Run an image that carries them, or correct the tenant's suites in the control plane.");
        }

        return [.. carried.Where(suite => named.Contains(suite.ShortName, StringComparer.Ordinal))];
    }

    /// <summary>The short names of a configuration value: <c>procurement,school</c>; every name must be a suite's short name.</summary>
    public static IReadOnlyList<string> Parse(string? value)
    {
        var names = (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        var invalid = names.FirstOrDefault(name => !SuiteName.IsShortName(name));
        return invalid is null
            ? names
            : throw new InvalidOperationException($"'{invalid}' in {ConfigurationKey} is not a suite's short name (ADR-0043).");
    }

    /// <summary>The configuration value of these suites, as the fleet writes it: their short names in order, separated by commas.</summary>
    public static string Value(IEnumerable<string> suites)
    {
        ArgumentNullException.ThrowIfNull(suites);
        return string.Join(',', suites.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>The Foundation's modules, then each installed suite's, in that order.</summary>
    public static IReadOnlyList<IModule> Compose(IReadOnlyList<IModule> foundation, IReadOnlyList<SuiteDefinition> installed)
    {
        ArgumentNullException.ThrowIfNull(foundation);
        ArgumentNullException.ThrowIfNull(installed);
        return [.. foundation, .. installed.SelectMany(suite => suite.Modules())];
    }
}

/// <summary>
/// The suites this unit runs, with what each is (TF-244): what <c>/me/context</c> names and a suite's About page shows. A
/// host without suites, such as the control plane, runs none.
/// </summary>
public sealed class UnitSuites(IReadOnlyList<SuiteDefinition> installed)
{
    public static UnitSuites None { get; } = new([]);

    public IReadOnlyList<SuiteDefinition> Installed { get; } = installed;

    public SuiteDefinition? Find(string shortName) => Installed.FirstOrDefault(suite => string.Equals(suite.ShortName, shortName, StringComparison.Ordinal));
}
