using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Entitlements;
using Iron.Kernel.Files;
using Iron.Kernel.Identity;

namespace Iron.Host.Api;

/// <summary>
/// The modules a tenant unit runs, in order: Audit first (the others write events through its contract), then
/// Authorization, then Identity (it replaces the default principal), then Files, then Entitlements. The same list is
/// the source of the licensing catalogue the control plane carries (ADR-0034), so a module added here is licensable
/// there once <c>make catalogue</c> has written the file; a host test fails until it has.
/// </summary>
public static class UnitModules
{
    public const string CatalogueCommand = "catalogue";

    public static IReadOnlyList<IModule> All() => [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new FilesModule(), new EntitlementsModule()];

    public static bool IsCatalogueCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && string.Equals(args[0], CatalogueCommand, StringComparison.Ordinal);
    }

    /// <summary><c>catalogue [file]</c>: the licensing catalogue of this build, to the file or to standard output. Builds no host.</summary>
    public static async Task<int> WriteCatalogueAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var json = LicensingCatalogue.From(All()).ToJson();
        if (args.Length > 1)
        {
            await File.WriteAllTextAsync(args[1], json).ConfigureAwait(false);
            await Console.Out.WriteLineAsync($"Wrote the licensing catalogue to {args[1]}.").ConfigureAwait(false);
        }
        else
        {
            await Console.Out.WriteAsync(json).ConfigureAwait(false);
        }

        return 0;
    }
}
