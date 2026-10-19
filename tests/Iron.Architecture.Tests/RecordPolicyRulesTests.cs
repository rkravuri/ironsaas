using System.Reflection;
using Iron.Kernel;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Policies;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Architecture.Tests;

/// <summary>Every business record of every module has a registered policy (TF-035 AC3, ADR-0032), and a fixture proves the refusal.</summary>
public class RecordPolicyRulesTests
{
    [Fact]
    public void EveryBusinessRecordOfEveryModuleHasARegisteredPolicy()
    {
        var violations = new List<string>();
        var records = 0;
        foreach (var project in ProjectCatalog.Load(Repository.Root).Where(p => p.Kind == ProjectKind.Module))
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, project.Name + ".dll");
            if (!File.Exists(assemblyPath))
            {
                continue; // AssemblyReferenceTests already fails for a missing assembly
            }

            var assembly = Assembly.LoadFrom(assemblyPath);
            records += RecordPolicyRules.BusinessRecordTypes(assembly).Count;
            violations.AddRange(RecordPolicyRules.Check(assembly));
        }

        // No kernel module declares a business record today (A3, step 6); the first business or products module will.
        ArchitectureAssert.NoViolations($"Record policies over {records} business record type(s) (TF-035, ADR-0032)", violations);
    }

    [Fact]
    public void ABusinessRecordWithoutAPolicyIsRefusedAndOneWithAPolicyPasses()
    {
        var violations = RecordPolicyRules.Check(typeof(RecordPolicyRulesTests).Assembly);

        var violation = Assert.Single(violations);
        Assert.Contains($"{nameof(UnpolicedRecord)} is a business record with no record policy", violation, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PolicedRecord), violation, StringComparison.Ordinal);
        Assert.Contains(typeof(PolicedRecord), RecordPolicyRules.PolicedTypes(typeof(RecordPolicyRulesTests).Assembly));
    }

    [Fact]
    public void TheRuleNamesEveryRecordWithoutAPolicy()
    {
        var violations = RecordPolicyRules.Unpoliced("Iron.Products.Sample", [typeof(PolicedRecord), typeof(UnpolicedRecord)], [typeof(PolicedRecord)]);

        Assert.Equal(["Iron.Products.Sample: UnpolicedRecord is a business record with no record policy. Register one in the module's AddServices with AddRecordPolicy (TF-035, ADR-0032)."], violations);
    }
}

/// <summary>A fixture record whose module registers its policy.</summary>
public sealed class PolicedRecord : BusinessRecord
{
    public PolicedRecord(Guid owningOrganisationId)
        : base(owningOrganisationId)
    {
    }
}

/// <summary>A fixture record that nobody registers a policy for: the rule must name it.</summary>
public sealed class UnpolicedRecord : BusinessRecord
{
    public UnpolicedRecord(Guid owningOrganisationId)
        : base(owningOrganisationId)
    {
    }
}

/// <summary>A fixture module, as a module registers its policies in AddServices.</summary>
public sealed class FixtureModule : IModule
{
    private static readonly Permission Read = new("fixture.records.read", "View fixture records", Risk.Low);

    public string Name => "fixture";

    public IReadOnlyList<Permission> Permissions => [Read];

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddRecordPolicy("fixture_record", Read, RecordRules.UnitVisibility<PolicedRecord>(Read));

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
