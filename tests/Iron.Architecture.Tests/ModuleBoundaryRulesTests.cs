namespace Iron.Architecture.Tests;

public class ModuleBoundaryRulesTests
{
    [Fact]
    public void AModuleMayUseSharedLibrariesAndContracts()
    {
        var projects = new[]
        {
            Project("src/Kernel/Iron.Kernel/Iron.Kernel.csproj"),
            Project("src/Kernel/Iron.Kernel.Data/Iron.Kernel.Data.csproj", "Iron.Kernel"),
            Project("src/Kernel/Iron.Kernel.Identity/Contracts/Iron.Kernel.Identity.Contracts.csproj", "Iron.Kernel"),
            Project("src/Kernel/Iron.Kernel.Audit/Contracts/Iron.Kernel.Audit.Contracts.csproj", "Iron.Kernel"),
            Project("src/Kernel/Iron.Kernel.Audit/Iron.Kernel.Audit.csproj", "Iron.Kernel", "Iron.Kernel.Data", "Iron.Kernel.Audit.Contracts", "Iron.Kernel.Identity.Contracts"),
        };

        Assert.Empty(ModuleBoundaryRules.Check(projects));
    }

    [Fact]
    public void AModuleMustNotReferenceAnotherModule()
    {
        var projects = new[]
        {
            Project("src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj"),
            Project("src/Kernel/Iron.Kernel.Audit/Iron.Kernel.Audit.csproj", "Iron.Kernel.Identity"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("src/Kernel/Iron.Kernel.Audit/Iron.Kernel.Audit.csproj", violation, StringComparison.Ordinal);
        Assert.Contains("src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AContractsProjectMayReferenceIronCoreOnly()
    {
        var projects = new[]
        {
            Project("src/Kernel/Iron.Kernel/Iron.Kernel.csproj"),
            Project("src/Kernel/Iron.Kernel.Data/Iron.Kernel.Data.csproj", "Iron.Kernel"),
            Project("src/Kernel/Iron.Kernel.Identity/Contracts/Iron.Kernel.Identity.Contracts.csproj", "Iron.Kernel", "Iron.Kernel.Data"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("Iron.Kernel.Identity.Contracts.csproj", violation, StringComparison.Ordinal);
        Assert.Contains("Iron.Kernel.Data.csproj", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void ASharedLibraryMustNotReferenceAModule()
    {
        var projects = new[]
        {
            Project("src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj"),
            Project("src/Kernel/Iron.Kernel.Data/Iron.Kernel.Data.csproj", "Iron.Kernel.Identity"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("src/Kernel/Iron.Kernel.Data/Iron.Kernel.Data.csproj", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostMayReferenceModulesButNotAnotherHost()
    {
        var projects = new[]
        {
            Project("src/Kernel/Iron.Kernel/Iron.Kernel.csproj"),
            Project("src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj", "Iron.Kernel"),
            Project("src/Hosts/Iron.Host.Worker/Iron.Host.Worker.csproj", "Iron.Kernel"),
            Project("src/Hosts/Iron.Host.Api/Iron.Host.Api.csproj", "Iron.Kernel", "Iron.Kernel.Identity", "Iron.Host.Worker"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("src/Hosts/Iron.Host.Api/Iron.Host.Api.csproj", violation, StringComparison.Ordinal);
        Assert.Contains("Iron.Host.Worker.csproj", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AProductFollowsTheSameRulesAsAModule()
    {
        var projects = new[]
        {
            Project("src/Kernel/Iron.Kernel/Iron.Kernel.csproj"),
            Project("src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj", "Iron.Kernel"),
            Project("src/Kernel/Iron.Kernel.Identity/Contracts/Iron.Kernel.Identity.Contracts.csproj", "Iron.Kernel"),
            Project("products/Procurement/Contracts/Procurement.Contracts.csproj", "Iron.Kernel"),
            Project("products/Procurement/Procurement.csproj", "Iron.Kernel", "Procurement.Contracts", "Iron.Kernel.Identity.Contracts", "Iron.Kernel.Identity"),
        };

        var violation = Assert.Single(ModuleBoundaryRules.Check(projects));

        Assert.Contains("products/Procurement/Procurement.csproj", violation, StringComparison.Ordinal);
        Assert.Contains("src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceToAnUnknownProjectIsReported()
    {
        var projects = new[]
        {
            Project("src/Kernel/Iron.Kernel.Audit/Iron.Kernel.Audit.csproj", "Iron.Nowhere"),
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
