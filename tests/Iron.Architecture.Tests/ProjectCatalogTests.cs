namespace Iron.Architecture.Tests;

public class ProjectCatalogTests
{
    [Theory]
    [Proves("ADR-0029", "ADR-0043")]
    [InlineData("src/Kernel/Iron.Kernel/Iron.Kernel.csproj", "Kernel", ProjectKind.Vocabulary, null)]
    [InlineData("src/Kernel/Iron.Kernel.Data/Iron.Kernel.Data.csproj", "Kernel", ProjectKind.Library, null)]
    [InlineData("src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj", "Kernel", ProjectKind.Module, "Iron.Kernel.Identity")]
    [InlineData("src/Kernel/Iron.Kernel.Identity/Contracts/Iron.Kernel.Identity.Contracts.csproj", "Kernel", ProjectKind.Contracts, "Iron.Kernel.Identity")]
    [InlineData("src/Business/Iron.Business.Organisations/Iron.Business.Organisations.csproj", "Business", ProjectKind.Module, "Iron.Business.Organisations")]
    [InlineData("src/Business/Iron.Business.Organisations/Contracts/Iron.Business.Organisations.Contracts.csproj", "Business", ProjectKind.Contracts, "Iron.Business.Organisations")]
    [InlineData("src/Suites/Procurement/Iron.Suites.Procurement.Orders/Iron.Suites.Procurement.Orders.csproj", "Suite", ProjectKind.Module, "Iron.Suites.Procurement.Orders")]
    [InlineData("src/Suites/Procurement/Iron.Suites.Procurement.Orders/Contracts/Iron.Suites.Procurement.Orders.Contracts.csproj", "Suite", ProjectKind.Contracts, "Iron.Suites.Procurement.Orders")]
    [InlineData("src/Hosts/Iron.Host.Api/Iron.Host.Api.csproj", "Host", ProjectKind.Host, null)]
    [InlineData("tests/Iron.Architecture.Tests/Iron.Architecture.Tests.csproj", "Test", ProjectKind.Test, null)]
    [InlineData("tools/Iron.Smoke/Iron.Smoke.csproj", "Tool", ProjectKind.Tool, null)]
    [InlineData("src/Kernel/Iron.Kernel.Data/Iron.Kernel.Data.csproj", null, ProjectKind.Unknown, null)]
    [InlineData("src/Kernel/Iron.Kernel.Data/Iron.Kernel.Data.csproj", "Kernal", ProjectKind.Unknown, null)]
    public void ClassifiesAProjectByItsDeclaredLayerAndItsName(string relativePath, string? layer, ProjectKind expectedKind, string? expectedModule)
    {
        var project = Fixture.Declaring(relativePath, layer);

        Assert.Equal(expectedKind, project.Kind);
        Assert.Equal(expectedModule, project.ModuleName);
    }

    [Fact]
    [Proves("ADR-0029")]
    public void ReadsTheDeclaredLayerAsWritten()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IronLayer>kernel</IronLayer>
              </PropertyGroup>
            </Project>
            """;

        var project = ProjectCatalog.Describe("src/Kernel/Iron.Kernel.Data/Iron.Kernel.Data.csproj", xml);

        Assert.Equal("kernel", project.DeclaredLayer);
        Assert.Null(project.Layer);
    }

    [Fact]
    [Proves("ADR-0043")]
    public void ReadsTheDeclaredSuiteAsWritten()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IronLayer>Suite</IronLayer>
                <IronSuite>Procurement</IronSuite>
              </PropertyGroup>
            </Project>
            """;

        var project = ProjectCatalog.Describe("src/Suites/Procurement/Iron.Suites.Procurement.Orders/Iron.Suites.Procurement.Orders.csproj", xml);

        Assert.Equal(ProjectLayer.Suite, project.Layer);
        Assert.Equal("Procurement", project.DeclaredSuite);
        Assert.Null(Fixture.At("src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj").DeclaredSuite);
    }

    [Fact]
    [Proves("ADR-0001")]
    public void ReadsProjectReferencesByName()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../Iron.Kernel/Iron.Kernel.csproj" />
                <ProjectReference Include="..\Iron.Kernel.Data\Iron.Kernel.Data.csproj" />
                <ProjectReference Include="Contracts/Iron.Kernel.Audit.Contracts.csproj" />
              </ItemGroup>
            </Project>
            """;

        var project = ProjectCatalog.Describe("src/Kernel/Iron.Kernel.Audit/Iron.Kernel.Audit.csproj", xml);

        string[] expected = ["Iron.Kernel", "Iron.Kernel.Data", "Iron.Kernel.Audit.Contracts"];
        Assert.Equal(expected, project.References);
    }

    [Fact]
    [Proves("ADR-0001")]
    public void LoadsEveryProjectOfTheRepository()
    {
        var projects = ProjectCatalog.Load(Repository.Root);

        Assert.Contains(projects, p => p.Name == "Iron.Kernel" && p.Kind == ProjectKind.Vocabulary);
        Assert.Contains(projects, p => p.Name == "Iron.Kernel.Data" && p.Kind == ProjectKind.Library);
        Assert.Contains(projects, p => p.Name == "Iron.Kernel.Identity" && p.Kind == ProjectKind.Module);
        Assert.Contains(projects, p => p.Name == "Iron.Kernel.Identity.Contracts" && p.Kind == ProjectKind.Contracts);
        Assert.Contains(projects, p => p.Name == "Iron.Host.Api" && p.Kind == ProjectKind.Host);
        Assert.Contains(projects, p => p.Name == "Iron.Architecture.Tests" && p.Kind == ProjectKind.Test);
        Assert.Contains(projects, p => p.Name == "Iron.Smoke" && p.Kind == ProjectKind.Tool);
    }

    [Fact]
    [Proves("ADR-0029")]
    public void LeavesBuildOutputAndPackagesOut()
    {
        var projects = ProjectCatalog.Load(Repository.Root);

        Assert.DoesNotContain(projects, p => p.RelativePath.Split('/').Any(segment => ProjectCatalog.SkippedFolders.Contains(segment, StringComparer.Ordinal)));
    }
}
