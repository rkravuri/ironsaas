using Iron.Kernel.Api.Modules;
using Iron.Kernel.Tenancy.Data;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>
/// What the units of this build can license (ADR-0034): the file <c>licensing-catalogue.json</c> beside this class,
/// which <c>make catalogue</c> writes from the unit host's modules and a host test keeps equal to them, embedded in
/// this assembly so the control plane image carries it. The control plane never loads a Business or Products module.
/// </summary>
public sealed class UnitCatalogue(LicensingCatalogue catalogue)
{
    public const string ResourceName = "Iron.Kernel.Tenancy.Services.licensing-catalogue.json";

    public LicensingCatalogue Catalogue { get; } = catalogue;

    public static UnitCatalogue Load()
    {
        using var stream = typeof(UnitCatalogue).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The licensing catalogue {ResourceName} is not embedded in {typeof(UnitCatalogue).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return new UnitCatalogue(LicensingCatalogue.Parse(reader.ReadToEnd()));
    }

    /// <summary>
    /// TF-042 AC3: one message per code the catalogue does not know. A platform module gets its own message, since the
    /// kernel is always on and never in a plan.
    /// </summary>
    public IReadOnlyList<string> Unknown(IEnumerable<string> modules, IEnumerable<string> features)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(features);
        var problems = new List<string>();
        foreach (var module in modules.Distinct(StringComparer.Ordinal))
        {
            if (Catalogue.FindModule(module) is not null)
            {
                continue;
            }

            problems.Add(Catalogue.Platform.Contains(module, StringComparer.Ordinal)
                ? $"'{module}' is a platform module: it is always on and is never in a plan."
                : $"'{module}' is not a licensed module of this build.");
        }

        problems.AddRange(features.Distinct(StringComparer.Ordinal).Where(f => Catalogue.FindFeature(f) is null).Select(f => $"'{f}' is not a feature of this build."));
        return problems;
    }

    /// <summary>The same check for a plan as it is stored, before it is applied: a plan saved by an older build may name what this one dropped.</summary>
    public IReadOnlyList<string> Unknown(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Unknown(plan.Modules.Select(m => m.Code), plan.Features);
    }
}
