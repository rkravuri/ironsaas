using System.Reflection;
using System.Security.Cryptography;
using Iron.Kernel.Api.Modules;

namespace Iron.Architecture.Tests;

/// <summary>
/// Every module has its threat model before it is built (TF-077 AC2, ADR-0055): each module the repository's projects
/// declare is named by a record in docs/security/threat-models, in the template's form, and a record names only
/// modules that exist. The host tests hold the anonymous endpoints to the same records.
/// </summary>
public class ThreatModelTests
{
    [Fact]
    [Proves("TF-077 AC2", "ADR-0055")]
    public void EveryModuleIsNamedByAThreatModelRecordInTheTemplatesForm()
    {
        var modules = Modules().Select(module => module.Name).ToList();
        var records = ThreatModels.Read(ThreatModels.Folder());

        Assert.True(modules.Count >= 13, $"Only {modules.Count} modules were found; the search lost its way.");
        ArchitectureAssert.NoViolations(
            $"Threat models of {modules.Count} modules in {records.Count} records (TF-077 AC2)",
            [.. ThreatModels.FormProblems(records), .. ThreatModels.Coverage(records, "module", modules, record => record.Modules)]);
    }

    [Fact]
    [Proves("TF-077 AC2", "ADR-0055")]
    public void AModuleOrAnEndpointWithoutARecordFailsNamingItAndARecordOutOfFormFailsToo()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"threat-models-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "0000-template.md"), "- **Modules:** the module names, or none\n");
        File.WriteAllText(Path.Combine(folder, "sign-in.md"), Record("identity, retired", "`POST /auth/login`", ThreatModels.Categories));
        File.WriteAllText(Path.Combine(folder, "short.md"), Record("none", "none", ThreatModels.Categories.Where(c => c != "Repudiation")));
        File.WriteAllText(Path.Combine(folder, "headless.md"), "# Threat model: no heading\n\n" + Table(ThreatModels.Categories));

        var records = ThreatModels.Read(folder);
        var modules = ThreatModels.Coverage(records, "module", ["identity", "keys"], record => record.Modules);
        var endpoints = ThreatModels.Coverage(records, "anonymous endpoint", ["POST /auth/login", "POST /auth/new-door"], record => record.Endpoints);
        var form = ThreatModels.FormProblems(records);

        Assert.Equal(3, records.Count);
        Assert.Equal(
            [
                "the module 'keys' has no threat model record in docs/security/threat-models: write one from 0000-template.md before it is built (TF-077 AC2)",
                "sign-in.md names the module 'retired', which does not exist: correct the record (ADR-0055)",
            ],
            modules);
        Assert.Equal(["the anonymous endpoint 'POST /auth/new-door' has no threat model record in docs/security/threat-models: write one from 0000-template.md before it is built (TF-077 AC2)"], endpoints);
        Assert.Equal(
            [
                "headless.md lacks a line of the template's heading (- **Modules:**, - **Endpoints:**, - **Reviewed:**): copy 0000-template.md (ADR-0055)",
                "short.md has no threat of the category 'Repudiation' (ADR-0055)",
            ],
            form);
    }

    private static string Record(string modules, string endpoints, IEnumerable<string> categories) =>
        $"# Threat model: a fixture\n\n- **Modules:** {modules}\n- **Endpoints:** {endpoints}\n- **Reviewed:** 2031-01-01 (a test)\n\n## Threats\n\n" + Table(categories);

    private static string Table(IEnumerable<string> categories) =>
        "| Category | Threat | Controls | Requirements |\n|---|---|---|---|\n" + string.Concat(categories.Select(category => $"| {category} | A threat. | A control. | TF-001 |\n"));

    /// <summary>Every module the repository's module projects declare, from their built assemblies beside this test.</summary>
    private static IEnumerable<IModule> Modules() =>
        ProjectCatalog.Load(Repository.Root)
            .Where(project => project.Kind is ProjectKind.Module)
            .Select(project => Path.Combine(AppContext.BaseDirectory, project.Name + ".dll"))
            .Where(File.Exists)
            .Select(Assembly.LoadFrom)
            .SelectMany(LicensingRules.ModulesOf);
}
