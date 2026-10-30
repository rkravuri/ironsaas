using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Changes;
using Iron.Kernel.Compliance;
using Iron.Kernel.Entitlements;
using Iron.Kernel.Files;
using Iron.Kernel.Flags;
using Iron.Kernel.Identity;
using Iron.Kernel.Keys;
using Iron.Kernel.Monitoring;
using Iron.Kernel.Protection;
using Iron.Kernel.Samples;
using Iron.Kernel.Settings;
using Iron.Kernel.Visibility;
using Iron.Suites.Procurement.Purchasing;

namespace Iron.Host.Api;

/// <summary>
/// The modules a tenant unit runs, in order: Audit first (the others write events through its contract), then
/// Authorization, then Identity (it replaces the default principal), then Files, Entitlements, Flags and Settings, then
/// Visibility, whose tags narrow what every record policy shows (ADR-0057), then Compliance, which signs the records of
/// the modules before it, then Changes, which holds the sensitive changes of the modules before it for a second
/// administrator (ADR-0052), then Protection, then Keys, which moves every module's encrypted values on a rotation
/// (ADR-0054), then Monitoring, whose rules count every module's audit events (ADR-0056); then the modules of the
/// tenant's suites (TF-244, ADR-0059). Every module of the Foundation and of every suite is the source of the licensing
/// catalogue the control plane carries (ADR-0034), so a module added here is licensable there once <c>make catalogue</c>
/// has written the file; a host test fails until it has.
/// </summary>
public static class UnitModules
{
    public const string CatalogueCommand = "catalogue";

    /// <summary>The Foundation's modules: what every tenant's unit runs.</summary>
    public static IReadOnlyList<IModule> Foundation() => [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new FilesModule(), new EntitlementsModule(), new FlagsModule(), new SettingsModule(), new VisibilityModule(), new ComplianceModule(), new ChangesModule(), new ProtectionModule(), new KeysModule(), new MonitoringModule()];

    /// <summary>The suites this image carries (ADR-0043): one image for the Foundation and every suite.</summary>
    public static IReadOnlyList<SuiteDefinition> Suites() => [PurchasingModule.Suite()];

    /// <summary>Every module of the build, the Foundation's and every suite's: the catalogue and the configuration are made from it.</summary>
    public static IReadOnlyList<IModule> All() => InstalledSuites.Compose(Foundation(), Suites());

    /// <summary>What this build can license, switch and set, with the plans its suites bring (ADR-0034, TF-244): the file the control plane carries.</summary>
    public static LicensingCatalogue Catalogue() => LicensingCatalogue.From(All(), Suites());

    /// <summary>The suites this unit runs: those of <see cref="Suites"/> its configuration names in <c>Suites:Installed</c> (TF-244).</summary>
    public static IReadOnlyList<SuiteDefinition> InstalledFor(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        InstalledSuites.From(configuration, Suites());

    /// <summary>
    /// The modules this unit runs: the Foundation's, its suites' (TF-244: a suite it does not have migrates, seeds and
    /// maps nothing), and the sample module where the unit's configuration switches it on, which the fleet does for a
    /// tenant the operator marks for testing (ADR-0057). The sample module is never licensable, so the catalogue is made
    /// from <see cref="All"/> alone.
    /// </summary>
    public static IReadOnlyList<IModule> ForUnit(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var modules = InstalledSuites.Compose(Foundation(), InstalledFor(configuration));
        return SamplesModule.IsEnabled(configuration) ? [.. modules, new SamplesModule()] : modules;
    }

    public static bool IsCatalogueCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && string.Equals(args[0], CatalogueCommand, StringComparison.Ordinal);
    }

    /// <summary><c>version</c>: the release this image was stamped with, or <c>development</c> (ADR-0052). The fleet asks it at each update.</summary>
    public const string VersionCommand = "version";

    /// <summary><c>configuration</c>: this build's controlled configuration and its hashes (TF-218, ADR-0052), for the installation record.</summary>
    public const string ConfigurationCommand = "configuration";

    public static bool IsReleaseCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length == 1 && args[0] is VersionCommand or ConfigurationCommand;
    }

    /// <summary>The version the image carries in <c>Release__Version</c>, which its Dockerfile sets from the release's build argument.</summary>
    public static string ReleaseVersion() =>
        Environment.GetEnvironmentVariable("Release__Version") is { Length: > 0 } version ? version : "development";

    /// <summary>
    /// The controlled configuration of this build (TF-218, ADR-0052): the licensing catalogue's hash (the bytes <c>make
    /// catalogue</c> writes), the seeds' (the system roles and their permissions, in the canonical text the installation
    /// record also reads from a tenant's database), and the permission catalogue's. A unit's seeds hold the permissions of
    /// the modules it runs, so they are stated for every composition a unit can run (ADR-0057, ADR-0059):
    /// <c>seeds_sha256</c> for the Foundation alone, <c>sample_seeds_sha256</c> with the sample module, and
    /// <c>seeds_by_composition</c> for each set of the carried suites. Builds no host and needs no store.
    /// </summary>
    public static async Task<int> WriteReleaseAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args[0] == VersionCommand)
        {
            await Console.Out.WriteLineAsync(ReleaseVersion()).ConfigureAwait(false);
            return 0;
        }

        var catalogue = Catalogue().ToJson();
        var permissions = PermissionsOf(All());
        var seeds = SeedsText(PermissionsOf(Foundation()));
        var permissionText = string.Concat(permissions.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}:{p.Risk.ToString().ToLowerInvariant()}\n"));
        var description = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["version"] = ReleaseVersion(),
            ["catalogue_sha256"] = Sha256(catalogue),
            ["seeds_sha256"] = Sha256(seeds),
            ["permissions_sha256"] = Sha256(permissionText),
            ["seeds"] = seeds.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            ["sample_seeds_sha256"] = Sha256(SeedsText(PermissionsOf([.. Foundation(), new SamplesModule()]))),
            ["seeds_by_composition"] = SeedsByComposition(),
        };
        await Console.Out.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(description, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// The seeds' hash of every composition a unit can run (TF-244, ADR-0059): the Foundation alone or with any set of the
    /// carried suites, each without and with the sample module. The key names the suites in order, joined by <c>+</c>, or
    /// <c>foundation</c> for none, then <c>+samples</c> where the sample module runs; the installation record reads the
    /// tenant's suites from its schemas and checks its seeds against the matching key.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SeedsByComposition()
    {
        var suites = Suites();
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var mask = 0; mask < 1 << suites.Count; mask++)
        {
            var chosen = suites.Where((_, index) => (mask & (1 << index)) != 0).ToList();
            var key = chosen.Count == 0 ? Iron.Kernel.SuiteName.Foundation : string.Join('+', chosen.Select(s => s.ShortName).Order(StringComparer.Ordinal));
            var modules = InstalledSuites.Compose(Foundation(), chosen);
            hashes[key] = Sha256(SeedsText(PermissionsOf(modules)));
            hashes[key + "+samples"] = Sha256(SeedsText(PermissionsOf([.. modules, new SamplesModule()])));
        }

        return hashes;
    }

    private static IReadOnlyList<Iron.Kernel.Permission> PermissionsOf(IEnumerable<IModule> modules) =>
        new Iron.Kernel.Api.Permissions.PermissionCatalogue(modules.SelectMany(m => m.Permissions)).All;

    private static string SeedsText(IReadOnlyList<Iron.Kernel.Permission> permissions) =>
        Iron.Kernel.Authorization.Contracts.SystemRoleSeeds.Text(
            Iron.Kernel.Authorization.Contracts.SystemRoleSeeds.Definitions(permissions).Select(r => (r.Code, (IEnumerable<string>)r.Permissions)));

    private static string Sha256(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// <c>catalogue [file]</c>: the catalogue of this build, to the file or to standard output. Builds no host. Writing a
    /// file keeps the flags it marks retired, and refuses, naming them, flags it marks active that the code no longer
    /// declares (TF-052 AC2, ADR-0035): retiring a flag is an explicit change of the file.
    /// </summary>
    public static async Task<int> WriteCatalogueAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var catalogue = Catalogue();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            var file = LicensingCatalogue.Parse(await File.ReadAllTextAsync(args[1]).ConfigureAwait(false));
            var problems = LicensingCatalogue.FlagLifecycleProblems(file, catalogue);
            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    await Console.Error.WriteLineAsync(problem).ConfigureAwait(false);
                }

                return 1;
            }

            catalogue = catalogue.WithRetiredFrom(file);
        }

        var json = catalogue.ToJson();
        if (args.Length > 1)
        {
            await File.WriteAllTextAsync(args[1], json).ConfigureAwait(false);
            await Console.Out.WriteLineAsync($"Wrote the catalogue to {args[1]}.").ConfigureAwait(false);

            // The shell's typed setting keys come from the same declarations (ADR-0036), so they are written together.
            if (args.Length > 2)
            {
                await File.WriteAllTextAsync(args[2], ShellSettingKeys.ToTypeScript(catalogue)).ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"Wrote the shell's setting keys to {args[2]}.").ConfigureAwait(false);
            }
        }
        else
        {
            await Console.Out.WriteAsync(json).ConfigureAwait(false);
        }

        return 0;
    }
}
