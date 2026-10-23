using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Changes;
using Iron.Kernel.Compliance;
using Iron.Kernel.Entitlements;
using Iron.Kernel.Files;
using Iron.Kernel.Flags;
using Iron.Kernel.Identity;
using Iron.Kernel.Settings;

namespace Iron.Host.Api;

/// <summary>
/// The modules a tenant unit runs, in order: Audit first (the others write events through its contract), then
/// Authorization, then Identity (it replaces the default principal), then Files, Entitlements, Flags and Settings, then
/// Compliance, which signs the records of the modules before it, then Changes, which holds the sensitive changes of
/// the modules before it for a second administrator (ADR-0052). The same list is
/// the source of the licensing catalogue the control plane carries (ADR-0034), so a module added here is licensable
/// there once <c>make catalogue</c> has written the file; a host test fails until it has.
/// </summary>
public static class UnitModules
{
    public const string CatalogueCommand = "catalogue";

    public static IReadOnlyList<IModule> All() => [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new FilesModule(), new EntitlementsModule(), new FlagsModule(), new SettingsModule(), new ComplianceModule(), new ChangesModule()];

    public static bool IsCatalogueCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && string.Equals(args[0], CatalogueCommand, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>catalogue [file]</c>: the catalogue of this build, to the file or to standard output. Builds no host. Writing a
    /// file keeps the flags it marks retired, and refuses, naming them, flags it marks active that the code no longer
    /// declares (TF-052 AC2, ADR-0035): retiring a flag is an explicit change of the file.
    /// </summary>
    public static async Task<int> WriteCatalogueAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var catalogue = LicensingCatalogue.From(All());
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
