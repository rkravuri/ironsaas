namespace Iron.Architecture.Tests;

public class ProjectCatalogTests
{
    private const string EmptyProject = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";

    [Theory]
    [InlineData("src/Kernel/Iron.Kernel/Iron.Kernel.csproj", ProjectKind.Shared, null)]
    [InlineData("src/Iron.Data/Iron.Data.csproj", ProjectKind.Shared, null)]
    [InlineData("src/Iron.Identity/Iron.Identity.csproj", ProjectKind.Module, "Iron.Identity")]
    [InlineData("src/Iron.Identity/Contracts/Iron.Identity.Contracts.csproj", ProjectKind.Contracts, "Iron.Identity")]
    [InlineData("src/Iron.Host.Api/Iron.Host.Api.csproj", ProjectKind.Host, null)]
    [InlineData("products/Procurement/Procurement.csproj", ProjectKind.Product, "Procurement")]
    [InlineData("products/Procurement/Contracts/Procurement.Contracts.csproj", ProjectKind.Contracts, "Procurement")]
    [InlineData("tests/Iron.Architecture.Tests/Iron.Architecture.Tests.csproj", ProjectKind.Test, null)]
    [InlineData("tools/Iron.Tools.Scaffold/Iron.Tools.Scaffold.csproj", ProjectKind.Tool, null)]
    public void ClassifiesAProjectByItsLocation(string relativePath, ProjectKind expectedKind, string? expectedModule)
    {
        var project = ProjectCatalog.Describe(relativePath, EmptyProject);

        Assert.Equal(expectedKind, project.Kind);
        Assert.Equal(expectedModule, project.ModuleName);
    }

    [Fact]
    public void ReadsProjectReferencesByName()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../Iron.Kernel/Iron.Kernel.csproj" />
                <ProjectReference Include="..\Iron.Data\Iron.Data.csproj" />
                <ProjectReference Include="Contracts/Iron.Audit.Contracts.csproj" />
              </ItemGroup>
            </Project>
            """;

        var project = ProjectCatalog.Describe("src/Iron.Audit/Iron.Audit.csproj", xml);

        string[] expected = ["Iron.Kernel", "Iron.Data", "Iron.Audit.Contracts"];
        Assert.Equal(expected, project.References);
    }

    [Fact]
    public void LoadsEveryProjectOfTheRepository()
    {
        var projects = ProjectCatalog.Load(Repository.Root);

        Assert.Contains(projects, p => p.Name == "Iron.Kernel" && p.Kind == ProjectKind.Shared);
        Assert.Contains(projects, p => p.Name == "Iron.Identity" && p.Kind == ProjectKind.Module);
        Assert.Contains(projects, p => p.Name == "Iron.Identity.Contracts" && p.Kind == ProjectKind.Contracts);
        Assert.Contains(projects, p => p.Name == "Iron.Host.Api" && p.Kind == ProjectKind.Host);
        Assert.Contains(projects, p => p.Name == "Iron.Architecture.Tests" && p.Kind == ProjectKind.Test);
    }
}
