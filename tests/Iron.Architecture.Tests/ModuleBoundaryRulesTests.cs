namespace Iron.Architecture.Tests;

public class ModuleBoundaryRulesTests
{
    [Fact]
    public void AModuleMayUseSharedLibrariesAndContracts()
    {
        var projects = new[]
        {
            Project("src/Kernel/Iron.Kernel/Iron.Kernel.csproj"),
            Project("src/Iron.Data/Iron.Data.csproj", "Iron.Kernel"),
            Project("src/Iron.Identity/Contracts/Iron.Identity.Contracts.csproj", "Iron.Kernel"),
            Project("src/Iron.Audit/Contracts/Iron.Audit.Contracts.csproj", "Iron.Kernel"),
            Project("src/Iron.Audit/Iron.Audit.csproj", "Iron.Kernel", "Iron.Data", "Iron.Audit.Contracts", "Iron.Identity.Contracts"),
        };

        Assert.Empty(ModuleBoundaryRules.Check(projects));
    }

    [Fact]
    public void AModuleMustNotReferenceAnotherModule()
    {
        var projects = new[]
        {
            Project("src/Iron.Identity/Iron.Identity.csproj"),
            Project("src/Iron.Audit/Iron.Audit.csproj", "Iron.Identity"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("src/Iron.Audit/Iron.Audit.csproj", violation, StringComparison.Ordinal);
        Assert.Contains("src/Iron.Identity/Iron.Identity.csproj", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AContractsProjectMayReferenceIronCoreOnly()
    {
        var projects = new[]
        {
            Project("src/Kernel/Iron.Kernel/Iron.Kernel.csproj"),
            Project("src/Iron.Data/Iron.Data.csproj", "Iron.Kernel"),
            Project("src/Iron.Identity/Contracts/Iron.Identity.Contracts.csproj", "Iron.Kernel", "Iron.Data"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("Iron.Identity.Contracts.csproj", violation, StringComparison.Ordinal);
        Assert.Contains("Iron.Data.csproj", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void ASharedLibraryMustNotReferenceAModule()
    {
        var projects = new[]
        {
            Project("src/Iron.Identity/Iron.Identity.csproj"),
            Project("src/Iron.Data/Iron.Data.csproj", "Iron.Identity"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("src/Iron.Data/Iron.Data.csproj", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostMayReferenceModulesButNotAnotherHost()
    {
        var projects = new[]
        {
            Project("src/Kernel/Iron.Kernel/Iron.Kernel.csproj"),
            Project("src/Iron.Identity/Iron.Identity.csproj", "Iron.Kernel"),
            Project("src/Iron.Host.Worker/Iron.Host.Worker.csproj", "Iron.Kernel"),
            Project("src/Iron.Host.Api/Iron.Host.Api.csproj", "Iron.Kernel", "Iron.Identity", "Iron.Host.Worker"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("src/Iron.Host.Api/Iron.Host.Api.csproj", violation, StringComparison.Ordinal);
        Assert.Contains("Iron.Host.Worker.csproj", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AProductFollowsTheSameRulesAsAModule()
    {
        var projects = new[]
        {
            Project("src/Kernel/Iron.Kernel/Iron.Kernel.csproj"),
            Project("src/Iron.Identity/Iron.Identity.csproj", "Iron.Kernel"),
            Project("src/Iron.Identity/Contracts/Iron.Identity.Contracts.csproj", "Iron.Kernel"),
            Project("products/Procurement/Contracts/Procurement.Contracts.csproj", "Iron.Kernel"),
            Project("products/Procurement/Procurement.csproj", "Iron.Kernel", "Procurement.Contracts", "Iron.Identity.Contracts", "Iron.Identity"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("products/Procurement/Procurement.csproj", violation, StringComparison.Ordinal);
        Assert.Contains("src/Iron.Identity/Iron.Identity.csproj", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceToAnUnknownProjectIsReported()
    {
        var projects = new[]
        {
            Project("src/Iron.Audit/Iron.Audit.csproj", "Iron.Nowhere"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("Iron.Nowhere", violation, StringComparison.Ordinal);
    }

    private static ProjectInfo Project(string relativePath, params string[] references)
    {
        var items = string.Concat(references.Select(r => $"<ProjectReference Include=\"../{r}/{r}.csproj\" />"));
        var xml = $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{items}</ItemGroup></Project>";
        return ProjectCatalog.Describe(relativePath, xml);
    }
}
