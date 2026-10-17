namespace Iron.Architecture.Tests;

public class AssemblyReferenceRulesTests
{
    [Fact]
    public void ReportsTypesInAnotherModulesPrivateNamespaces()
    {
        var referencedTypes = new List<(string Namespace, string Name)>
        {
            ("Iron.Identity.Data", "UserRow"),
            ("Iron.Identity.Data.Migrations", "InitialSchema"),
            ("Iron.Identity.Contracts", "IUserDirectory"),
            ("Iron.Identity", "IdentityModule"),
            ("Iron.Audit.Data", "AuditEventRow"),
            ("Iron.Data", "Entity"),
            ("System", "Object"),
        };

        var violations = AssemblyReferenceRules.FindForbidden(
            "Iron.Audit.dll",
            referencedTypes,
            ownModule: "Iron.Audit",
            allModules: ["Iron.Identity", "Iron.Audit"]);

        Assert.Collection(
            violations,
            first => Assert.Contains("Iron.Identity.Data.UserRow", first, StringComparison.Ordinal),
            second => Assert.Contains("Iron.Identity.Data.Migrations.InitialSchema", second, StringComparison.Ordinal));
    }

    [Fact]
    public void AHostMayUseModuleRootAndContractsTypesOnly()
    {
        var referencedTypes = new List<(string Namespace, string Name)>
        {
            ("Iron.Identity", "IdentityModule"),
            ("Iron.Identity.Contracts", "IUserDirectory"),
            ("Iron.Identity.Services", "UserService"),
        };

        var violations = AssemblyReferenceRules.FindForbidden(
            "Iron.Host.Api.dll",
            referencedTypes,
            ownModule: null,
            allModules: ["Iron.Identity"]);

        var violation = Assert.Single(violations);

        Assert.Contains("Iron.Identity.Services.UserService", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSharedDataLibraryIsNotMistakenForAModuleDataNamespace()
    {
        var referencedTypes = new List<(string Namespace, string Name)>
        {
            ("Iron.Data", "Entity"),
            ("Iron.Data.Conventions", "SnakeCaseNames"),
        };

        var violations = AssemblyReferenceRules.FindForbidden(
            "Iron.Audit.dll",
            referencedTypes,
            ownModule: "Iron.Audit",
            allModules: ["Iron.Identity", "Iron.Audit"]);

        Assert.Empty(violations);
    }

    [Fact]
    public void ReadsTypeReferencesFromACompiledAssembly()
    {
        var types = AssemblyReferenceRules.ReadReferencedTypes(typeof(AssemblyReferenceRules).Assembly.Location);

        Assert.Contains(("System", "Object"), types);
    }
}
