namespace Iron.Architecture.Tests;

public class AssemblyReferenceRulesTests
{
    [Fact]
    public void ReportsTypesInAnotherModulesPrivateNamespaces()
    {
        var referencedTypes = new List<(string Namespace, string Name)>
        {
            ("Iron.Kernel.Identity.Data", "UserRow"),
            ("Iron.Kernel.Identity.Data.Migrations", "InitialSchema"),
            ("Iron.Kernel.Identity.Contracts", "IUserDirectory"),
            ("Iron.Kernel.Identity", "IdentityModule"),
            ("Iron.Kernel.Audit.Data", "AuditEventRow"),
            ("Iron.Kernel.Data", "Entity"),
            ("System", "Object"),
        };

        var violations = AssemblyReferenceRules.FindForbidden(
            "Iron.Kernel.Audit.dll",
            referencedTypes,
            ownModule: "Iron.Kernel.Audit",
            allModules: ["Iron.Kernel.Identity", "Iron.Kernel.Audit"]);

        Assert.Collection(
            violations,
            first => Assert.Contains("Iron.Kernel.Identity.Data.UserRow", first, StringComparison.Ordinal),
            second => Assert.Contains("Iron.Kernel.Identity.Data.Migrations.InitialSchema", second, StringComparison.Ordinal));
    }

    [Fact]
    public void AHostMayUseModuleRootAndContractsTypesOnly()
    {
        var referencedTypes = new List<(string Namespace, string Name)>
        {
            ("Iron.Kernel.Identity", "IdentityModule"),
            ("Iron.Kernel.Identity.Contracts", "IUserDirectory"),
            ("Iron.Kernel.Identity.Services", "UserService"),
        };

        var violations = AssemblyReferenceRules.FindForbidden(
            "Iron.Host.Api.dll",
            referencedTypes,
            ownModule: null,
            allModules: ["Iron.Kernel.Identity"]);

        var violation = Assert.Single(violations);

        Assert.Contains("Iron.Kernel.Identity.Services.UserService", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSharedDataLibraryIsNotMistakenForAModuleDataNamespace()
    {
        var referencedTypes = new List<(string Namespace, string Name)>
        {
            ("Iron.Kernel.Data", "Entity"),
            ("Iron.Kernel.Data.Conventions", "SnakeCaseNames"),
        };

        var violations = AssemblyReferenceRules.FindForbidden(
            "Iron.Kernel.Audit.dll",
            referencedTypes,
            ownModule: "Iron.Kernel.Audit",
            allModules: ["Iron.Kernel.Identity", "Iron.Kernel.Audit"]);

        Assert.Empty(violations);
    }

    [Fact]
    public void ReadsTypeReferencesFromACompiledAssembly()
    {
        var types = AssemblyReferenceRules.ReadReferencedTypes(typeof(AssemblyReferenceRules).Assembly.Location);

        Assert.Contains(("System", "Object"), types);
    }
}
