using System.Reflection;
using Iron.Kernel;

namespace Iron.Architecture.Tests;

/// <summary>Kernel modules are never licensed; Business and suite modules always are (ADR-0034); a module's suite is its project's (ADR-0043).</summary>
public class LicensingRulesTests
{
    [Fact]
    public void EveryModuleDeclaresTheLicenceItsLayerAsksFor()
    {
        var violations = new List<string>();
        var modules = 0;
        foreach (var project in ProjectCatalog.Load(Repository.Root).Where(p => p is { Kind: ProjectKind.Module, Layer: not null }))
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, project.Name + ".dll");
            if (!File.Exists(assemblyPath))
            {
                continue; // AssemblyReferenceTests already fails for a missing assembly
            }

            foreach (var module in LicensingRules.ModulesOf(Assembly.LoadFrom(assemblyPath)))
            {
                modules++;
                violations.AddRange(LicensingRules.Check(project.Name, project.Layer!.Value, module.Name, module.Licence));
                violations.AddRange(LicensingRules.CheckSuite(project.Name, project.DeclaredSuite, module.Name, module.Suite));
            }
        }

        Assert.True(modules > 0, "No module was found to check.");
        ArchitectureAssert.NoViolations($"Licences of {modules} module(s) (ADR-0034)", violations);
    }

    [Fact]
    public void TheRuleRefusesALicensedKernelModuleAndAnUnlicensedSuiteModule()
    {
        Assert.Contains("never in a plan", Assert.Single(LicensingRules.Check("Iron.Kernel.Sample", ProjectLayer.Kernel, "sample", new ModuleLicence("sample", SubscriptionType.Platform))), StringComparison.Ordinal);
        Assert.Contains("the suite module 'sample' declares no licence", Assert.Single(LicensingRules.Check("Iron.Suites.Procurement.Sample", ProjectLayer.Suite, "sample", null)), StringComparison.Ordinal);
        Assert.Empty(LicensingRules.Check("Iron.Business.Sample", ProjectLayer.Business, "sample", new ModuleLicence("sample", SubscriptionType.PerPerson)));
        Assert.Empty(LicensingRules.Check("Iron.Kernel.Sample", ProjectLayer.Kernel, "sample", null));
    }

    [Fact]
    public void AModuleReportsTheSuiteItsProjectDeclares()
    {
        Assert.Empty(LicensingRules.CheckSuite("Iron.Suites.Procurement.Orders", "Procurement", "orders", "procurement"));
        Assert.Empty(LicensingRules.CheckSuite("Iron.Business.Items", null, "items", SuiteName.Foundation));
        Assert.Contains("reports the suite 'school'", Assert.Single(LicensingRules.CheckSuite("Iron.Suites.Procurement.Orders", "Procurement", "orders", "school")), StringComparison.Ordinal);
        Assert.Contains("its project declares 'foundation'", Assert.Single(LicensingRules.CheckSuite("Iron.Business.Items", null, "items", "procurement")), StringComparison.Ordinal);
    }
}
