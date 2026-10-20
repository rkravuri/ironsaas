namespace Iron.Architecture.Tests;

public class LayerRulesTests
{
    private const string Identity = "src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj";

    [Theory]
    [Proves("ADR-0029")]
    [InlineData(null)]
    [InlineData("")]
    public void AProjectWithoutALayerIsRefused(string? layer)
    {
        var violation = Assert.Single(LayerRules.Check([Fixture.Declaring(Identity, layer)]));

        Assert.StartsWith(Identity, violation, StringComparison.Ordinal);
        Assert.Contains("declares no <IronLayer>", violation, StringComparison.Ordinal);
    }

    [Theory]
    [Proves("ADR-0029")]
    [InlineData("Kernal")]
    [InlineData("kernel")]
    [InlineData(" Kernel ")]
    [InlineData("Shared")]
    public void AProjectWithAnUnknownLayerIsRefused(string layer)
    {
        var violation = Assert.Single(LayerRules.Check([Fixture.Declaring(Identity, layer)]));

        Assert.StartsWith(Identity, violation, StringComparison.Ordinal);
        Assert.Contains($"declares IronLayer '{layer}', which is not a layer", violation, StringComparison.Ordinal);
    }

    [Theory]
    [Proves("ADR-0029")]
    [InlineData("src/Kernel/Iron.Kernel/Iron.Kernel.csproj", "Kernel")]
    [InlineData("src/Kernel/Iron.Kernel.Data/Iron.Kernel.Data.csproj", "Kernel")]
    [InlineData("src/Kernel/Iron.Kernel.Identity/Contracts/Iron.Kernel.Identity.Contracts.csproj", "Kernel")]
    [InlineData("src/Business/Iron.Business.Organisations/Iron.Business.Organisations.csproj", "Business")]
    [InlineData("src/Hosts/Iron.Host.Api/Iron.Host.Api.csproj", "Host")]
    [InlineData("tests/Kernel/Iron.Kernel.Identity.Tests/Iron.Kernel.Identity.Tests.csproj", "Test")]
    [InlineData("tests/Iron.Architecture.Tests/Iron.Architecture.Tests.csproj", "Test")]
    [InlineData("tests/Iron.Testing/Iron.Testing.csproj", "Test")]
    [InlineData("tests/Iron.Testing.Analyzers/Iron.Testing.Analyzers.csproj", "Test")]
    [InlineData("tools/Iron.Smoke/Iron.Smoke.csproj", "Tool")]
    public void EveryLayerAcceptsItsOwnProjects(string relativePath, string layer)
    {
        Assert.Empty(LayerRules.Check([Fixture.Declaring(relativePath, layer)]));
    }

    [Theory]
    [Proves("ADR-0029")]
    [InlineData("src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj", "Business")]
    [InlineData("src/Business/Iron.Business.Items/Iron.Business.Items.csproj", "Kernel")]
    [InlineData("src/Business/Iron.Kernel.Items/Iron.Kernel.Items.csproj", "Business")]
    [InlineData("src/Suites/Procurement/Iron.Business.Items/Iron.Business.Items.csproj", "Business")]
    [InlineData("src/Hosts/Iron.Kernel.Api/Iron.Kernel.Api.csproj", "Host")]
    [InlineData("src/Kernel/Iron.Kernel.Identity.Tests/Iron.Kernel.Identity.Tests.csproj", "Test")]
    [InlineData("tests/Iron.Fixtures/Iron.Fixtures.csproj", "Test")]
    [InlineData("src/Kernel/Iron.Kernel.Testing/Iron.Kernel.Testing.csproj", "Test")]
    [InlineData("tests/Iron.Analyzers/Iron.Analyzers.csproj", "Test")]
    public void ALayerMustAgreeWithTheNameAndTheFolder(string relativePath, string layer)
    {
        var violation = Assert.Single(LayerRules.Check([Fixture.Declaring(relativePath, layer)]));

        Assert.StartsWith(relativePath, violation, StringComparison.Ordinal);
        Assert.Contains($"declares IronLayer {layer}, but a {layer} project is", violation, StringComparison.Ordinal);
    }

    [Theory]
    [Proves("ADR-0043")]
    [InlineData("src/Suites/Procurement/Iron.Suites.Procurement.Orders/Iron.Suites.Procurement.Orders.csproj")]
    [InlineData("src/Suites/Procurement/Iron.Suites.Procurement.Purchasing/Contracts/Iron.Suites.Procurement.Purchasing.Contracts.csproj")]
    [InlineData("src/Suites/School/Iron.Suites.School.Pupils/Iron.Suites.School.Pupils.csproj")]
    public void ASuiteAcceptsItsOwnProjects(string relativePath)
    {
        Assert.Empty(LayerRules.Check([Fixture.At(relativePath)]));
    }

    [Theory]
    [Proves("ADR-0043")]
    // The suite in IronSuite, the name and the folder must be one suite.
    [InlineData("src/Suites/Procurement/Iron.Suites.School.Pupils/Iron.Suites.School.Pupils.csproj", "Procurement")]
    [InlineData("src/Suites/School/Iron.Suites.Procurement.Orders/Iron.Suites.Procurement.Orders.csproj", "Procurement")]
    [InlineData("src/Suites/Procurement/Iron.Suites.Procurement.Orders/Iron.Suites.Procurement.Orders.csproj", "School")]
    [InlineData("src/Suites/Iron.Suites.Procurement.Orders/Iron.Suites.Procurement.Orders.csproj", "Procurement")]
    [InlineData("src/Suites/Procurement/Iron.Products.Orders/Iron.Products.Orders.csproj", "Procurement")]
    [InlineData("src/Suites/Procurement/Iron.Suites.Procurement/Iron.Suites.Procurement.csproj", "Procurement")]
    public void ASuiteProjectMustAgreeWithItsSuiteNameAndFolder(string relativePath, string suite)
    {
        var violation = Assert.Single(LayerRules.Check([Fixture.DeclaringSuite(relativePath, "Suite", suite)]));

        Assert.StartsWith(relativePath, violation, StringComparison.Ordinal);
        Assert.Contains($"declares IronLayer Suite and IronSuite {suite}, but a project of that suite is named Iron.Suites.{suite}.* and lives under src/Suites/{suite}/", violation, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0043")]
    public void ASuiteProjectWithoutASuiteIsRefused()
    {
        const string path = "src/Suites/Procurement/Iron.Suites.Procurement.Orders/Iron.Suites.Procurement.Orders.csproj";

        var violation = Assert.Single(LayerRules.Check([Fixture.Declaring(path, "Suite")]));

        Assert.Contains("declares IronLayer Suite but no <IronSuite>", violation, StringComparison.Ordinal);
    }

    [Theory]
    [Proves("ADR-0043")]
    [InlineData("procurement")]
    [InlineData("Kernel")]
    [InlineData("Business")]
    [InlineData("Foundation")]
    [InlineData("e-Procurement")]
    public void ASuiteNameIsOneWordInPascalCaseAndNotALayer(string suite)
    {
        var path = $"src/Suites/{suite}/Iron.Suites.{suite}.Orders/Iron.Suites.{suite}.Orders.csproj";

        var violation = Assert.Single(LayerRules.Check([Fixture.DeclaringSuite(path, "Suite", suite)]));

        Assert.Contains($"declares IronSuite '{suite}', which is not a suite name", violation, StringComparison.Ordinal);
    }

    [Theory]
    [Proves("ADR-0043")]
    [InlineData("src/Business/Iron.Business.Items/Iron.Business.Items.csproj", "Business")]
    [InlineData("src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj", "Kernel")]
    [InlineData("src/Hosts/Iron.Host.Api/Iron.Host.Api.csproj", "Host")]
    [InlineData("tests/Suites/Procurement/Iron.Suites.Procurement.Orders.Tests/Iron.Suites.Procurement.Orders.Tests.csproj", "Test")]
    public void OnlyASuiteProjectNamesASuite(string relativePath, string layer)
    {
        var violation = Assert.Single(LayerRules.Check([Fixture.DeclaringSuite(relativePath, layer, "Procurement")]));

        Assert.Contains($"declares IronSuite 'Procurement', but it is a {layer} project", violation, StringComparison.Ordinal);
    }
}
