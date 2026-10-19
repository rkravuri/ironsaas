using System.Reflection;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Policies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Architecture.Tests;

/// <summary>
/// TF-035 AC3 and ADR-0032: every concrete business record a module declares has a record policy that the module's
/// <c>AddServices</c> registers. With paging and reads by id that only go through a policy, a record without one cannot
/// be listed or opened, and this rule says so before anything runs.
/// </summary>
public static class RecordPolicyRules
{
    /// <summary>The concrete business record types an assembly declares.</summary>
    public static IReadOnlyList<Type> BusinessRecordTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(BusinessRecord).IsAssignableFrom(t)).ToList();
    }

    /// <summary>
    /// The record types the assembly's modules register a policy for, read from what their <c>AddServices</c> adds to an
    /// empty service collection with an empty configuration: registering a policy needs no configuration value.
    /// </summary>
    public static IReadOnlyList<Type> PolicedTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        foreach (var moduleType in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IModule).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null))
        {
            ((IModule)Activator.CreateInstance(moduleType)!).AddServices(services, configuration);
        }

        return services
            .Where(d => d.ServiceType == typeof(RecordPolicyRegistration))
            .Select(d => ((RecordPolicyRegistration)d.ImplementationInstance!).RecordType)
            .ToList();
    }

    /// <summary>Pure rule: the business records without a registered policy, one message each.</summary>
    public static IReadOnlyList<string> Unpoliced(string assemblyLabel, IEnumerable<Type> businessRecords, IEnumerable<Type> policed)
    {
        var registered = policed.ToHashSet();
        return businessRecords
            .Where(record => !registered.Contains(record))
            .Select(record => $"{assemblyLabel}: {record.Name} is a business record with no record policy. Register one in the module's AddServices with AddRecordPolicy (TF-035, ADR-0032).")
            .ToList();
    }

    /// <summary>The rule over one assembly; the modules' services are built only when the assembly declares business records.</summary>
    public static IReadOnlyList<string> Check(Assembly assembly)
    {
        var records = BusinessRecordTypes(assembly);
        return records.Count == 0 ? [] : Unpoliced(assembly.GetName().Name ?? "assembly", records, PolicedTypes(assembly));
    }
}
