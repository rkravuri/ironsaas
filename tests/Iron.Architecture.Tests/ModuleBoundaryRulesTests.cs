namespace Iron.Architecture.Tests;

public class ModuleBoundaryRulesTests
{
    private const string Vocabulary = "src/Kernel/Iron.Kernel/Iron.Kernel.csproj";
    private const string DataLibrary = "src/Kernel/Iron.Kernel.Data/Iron.Kernel.Data.csproj";
    private const string Identity = "src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj";
    private const string IdentityContracts = "src/Kernel/Iron.Kernel.Identity/Contracts/Iron.Kernel.Identity.Contracts.csproj";
    private const string Audit = "src/Kernel/Iron.Kernel.Audit/Iron.Kernel.Audit.csproj";
    private const string AuditContracts = "src/Kernel/Iron.Kernel.Audit/Contracts/Iron.Kernel.Audit.Contracts.csproj";
    private const string Organisations = "src/Business/Iron.Business.Organisations/Iron.Business.Organisations.csproj";
    private const string OrganisationsContracts = "src/Business/Iron.Business.Organisations/Contracts/Iron.Business.Organisations.Contracts.csproj";
    private const string Procurement = "src/Products/Iron.Products.Procurement/Iron.Products.Procurement.csproj";
    private const string ProcurementContracts = "src/Products/Iron.Products.Procurement/Contracts/Iron.Products.Procurement.Contracts.csproj";

    [Fact]
    public void AModuleMayUseTheVocabularyTheKernelLibrariesAndContracts()
    {
        var projects = new[]
        {
            Fixture.At(Vocabulary),
            Fixture.At(DataLibrary, "Iron.Kernel"),
            Fixture.At(IdentityContracts, "Iron.Kernel"),
            Fixture.At(AuditContracts, "Iron.Kernel"),
            Fixture.At(Audit, "Iron.Kernel", "Iron.Kernel.Data", "Iron.Kernel.Audit.Contracts", "Iron.Kernel.Identity.Contracts"),
        };

        Assert.Empty(ModuleBoundaryRules.Check(projects));
    }

    [Fact]
    public void AModuleMustNotReferenceAnotherModule()
    {
        var projects = new[]
        {
            Fixture.At(Identity),
            Fixture.At(Audit, "Iron.Kernel.Identity"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains(Audit, violation, StringComparison.Ordinal);
        Assert.Contains(Identity, violation, StringComparison.Ordinal);
        Assert.Contains("Contracts projects only", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AContractsProjectMayReferenceTheVocabularyOnly()
    {
        var projects = new[]
        {
            Fixture.At(Vocabulary),
            Fixture.At(DataLibrary, "Iron.Kernel"),
            Fixture.At(IdentityContracts, "Iron.Kernel", "Iron.Kernel.Data"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains(IdentityContracts, violation, StringComparison.Ordinal);
        Assert.Contains(DataLibrary, violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AKernelLibraryMustNotReferenceAModule()
    {
        var projects = new[]
        {
            Fixture.At(Identity),
            Fixture.At(DataLibrary, "Iron.Kernel.Identity"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains(DataLibrary, violation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVocabularyReferencesNothing()
    {
        var projects = new[]
        {
            Fixture.At(DataLibrary),
            Fixture.At(Vocabulary, "Iron.Kernel.Data"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("vocabulary references nothing", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostMayComposeEveryLayerButNotReferenceAnotherHost()
    {
        var projects = new[]
        {
            Fixture.At(Vocabulary),
            Fixture.At(Identity, "Iron.Kernel"),
            Fixture.At(Organisations, "Iron.Kernel"),
            Fixture.At(Procurement, "Iron.Kernel"),
            Fixture.At("src/Hosts/Iron.Host.Worker/Iron.Host.Worker.csproj", "Iron.Kernel"),
            Fixture.At("src/Hosts/Iron.Host.Api/Iron.Host.Api.csproj", "Iron.Kernel", "Iron.Kernel.Identity", "Iron.Business.Organisations", "Iron.Products.Procurement", "Iron.Host.Worker"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("src/Hosts/Iron.Host.Api/Iron.Host.Api.csproj", violation, StringComparison.Ordinal);
        Assert.Contains("Iron.Host.Worker.csproj", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKernelMustNotReferenceTheBusinessLayerNotEvenItsContracts()
    {
        var projects = new[]
        {
            Fixture.At(OrganisationsContracts),
            Fixture.At(Organisations),
            Fixture.At(Identity, "Iron.Business.Organisations.Contracts"),
            Fixture.At(DataLibrary, "Iron.Business.Organisations"),
        };

        var violations = ModuleBoundaryRules.Check(projects);

        Assert.Equal(2, violations.Count);
        Assert.All(violations, v => Assert.Contains("Dependencies go downward only: a Kernel project may not use the Business layer", v, StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith(Identity, StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith(DataLibrary, StringComparison.Ordinal));
    }

    [Fact]
    public void TheBusinessLayerMustNotReferenceAProduct()
    {
        var projects = new[]
        {
            Fixture.At(ProcurementContracts),
            Fixture.At(Organisations, "Iron.Products.Procurement.Contracts"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains(Organisations, violation, StringComparison.Ordinal);
        Assert.Contains("a Business project may not use the Products layer", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void ALowerLayerIsUsedThroughItsContracts()
    {
        // A business module implements a kernel extension point; a product uses business and kernel masters.
        var projects = new[]
        {
            Fixture.At(Vocabulary),
            Fixture.At(DataLibrary, "Iron.Kernel"),
            Fixture.At(IdentityContracts, "Iron.Kernel"),
            Fixture.At(OrganisationsContracts, "Iron.Kernel"),
            Fixture.At(ProcurementContracts, "Iron.Kernel"),
            Fixture.At(Organisations, "Iron.Kernel", "Iron.Kernel.Data", "Iron.Business.Organisations.Contracts", "Iron.Kernel.Identity.Contracts"),
            Fixture.At(Procurement, "Iron.Kernel", "Iron.Kernel.Data", "Iron.Products.Procurement.Contracts", "Iron.Business.Organisations.Contracts", "Iron.Kernel.Identity.Contracts"),
        };

        Assert.Empty(ModuleBoundaryRules.Check(projects));
    }

    [Fact]
    public void ALowerLayersModuleIsPrivateToo()
    {
        var projects = new[]
        {
            Fixture.At(Identity),
            Fixture.At(Organisations),
            Fixture.At(Organisations.Replace("Organisations", "Items", StringComparison.Ordinal), "Iron.Kernel.Identity"),
            Fixture.At(Procurement, "Iron.Business.Organisations"),
        };

        var violations = ModuleBoundaryRules.Check(projects);

        Assert.Equal(2, violations.Count);
        Assert.All(violations, v => Assert.Contains("Another module's Api, Services and Data are private", v, StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceToAnUnknownProjectIsReported()
    {
        var projects = new[]
        {
            Fixture.At(Audit, "Iron.Nowhere"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("Iron.Nowhere", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceToAProjectWithoutALayerIsRefused()
    {
        var projects = new[]
        {
            Fixture.Declaring(AuditContracts, layer: null),
            Fixture.At(Audit, "Iron.Kernel.Audit.Contracts"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.StartsWith(Audit, violation, StringComparison.Ordinal);
        Assert.Contains("declares no valid IronLayer", violation, StringComparison.Ordinal);
    }
}
