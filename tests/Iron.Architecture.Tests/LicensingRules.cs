using System.Reflection;
using Iron.Kernel;
using Iron.Kernel.Api.Modules;

namespace Iron.Architecture.Tests;

/// <summary>
/// ADR-0034: the kernel is the platform, always on and never in a plan, so a kernel module declares no licence; a
/// Business or Products module is licensed per module, in its own code, so it must declare one.
/// </summary>
public static class LicensingRules
{
    /// <summary>The modules an assembly declares, built with their parameterless constructors as a host lists them.</summary>
    public static IReadOnlyList<IModule> ModulesOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IModule).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IModule)Activator.CreateInstance(t)!)
            .ToList();
    }

    /// <summary>Pure rule: one message for a module whose licence does not fit its layer.</summary>
    public static IReadOnlyList<string> Check(string label, ProjectLayer layer, string moduleName, ModuleLicence? licence) => layer switch
    {
        ProjectLayer.Kernel when licence is not null => [$"{label}: the kernel module '{moduleName}' declares the licence '{licence.Code}'. Kernel modules are the platform and are never in a plan; license a feature instead (ADR-0034)."],
        ProjectLayer.Business or ProjectLayer.Products when licence is null => [$"{label}: the {layer.ToString().ToLowerInvariant()} module '{moduleName}' declares no licence. Declare its licence code and subscription type in its IModule (ADR-0034)."],
        _ => [],
    };
}
