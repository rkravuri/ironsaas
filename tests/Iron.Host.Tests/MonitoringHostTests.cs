using Iron.Kernel.Api.Modules;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Monitoring.Services;
using Microsoft.Extensions.Configuration;

namespace Iron.Host.Tests;

/// <summary>
/// The rules run in the hosts as they are (TF-079 AC4, ADR-0056): with no configuration of the operator's, every rule of
/// a unit counts event types the unit's modules declare, and every rule of the control plane the control plane's, so no
/// rule fails on the day it ships. The unit's worker lists the same modules as its API.
/// </summary>
public class MonitoringHostTests
{
    [Fact]
    [Proves("TF-079 AC4", "ADR-0056")]
    public void EveryRuleOfAUnitAndOfTheControlPlaneRunsWithTheTypesItsHostsModulesDeclare()
    {
        var none = new ConfigurationBuilder().Build();
        var problems = Problems(MonitoringScope.Unit, Iron.Host.Api.UnitModules.All(), none)
            .Concat(Problems(MonitoringScope.ControlPlane, Iron.Host.ControlPlane.ControlPlaneComposition.Modules(), none))
            .ToList();

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.NotEmpty(AlertRules.For(MonitoringScope.Unit));
        Assert.NotEmpty(AlertRules.For(MonitoringScope.ControlPlane));
    }

    private static IEnumerable<string> Problems(MonitoringScope scope, IReadOnlyList<IModule> modules, IConfiguration configuration)
    {
        var declared = modules.SelectMany(module => module.AuditEventTypes).Select(type => type.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in AlertRules.For(scope))
        {
            var resolved = AlertRuleResolver.Resolve(rule, configuration, null, declared);
            if (resolved.IsFailure)
            {
                yield return $"{scope} {rule.Name}: {resolved.Error.Message}";
            }
        }
    }
}
