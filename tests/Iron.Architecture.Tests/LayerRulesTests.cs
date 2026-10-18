namespace Iron.Architecture.Tests;

public class LayerRulesTests
{
    private const string Identity = "src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AProjectWithoutALayerIsRefused(string? layer)
    {
        var violation = Assert.Single(LayerRules.Check([Fixture.Declaring(Identity, layer)]));

        Assert.StartsWith(Identity, violation, StringComparison.Ordinal);
        Assert.Contains("declares no <IronLayer>", violation, StringComparison.Ordinal);
    }

    [Theory]
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
    [InlineData("src/Kernel/Iron.Kernel/Iron.Kernel.csproj", "Kernel")]
    [InlineData("src/Kernel/Iron.Kernel.Data/Iron.Kernel.Data.csproj", "Kernel")]
    [InlineData("src/Kernel/Iron.Kernel.Identity/Contracts/Iron.Kernel.Identity.Contracts.csproj", "Kernel")]
    [InlineData("src/Business/Iron.Business.Organisations/Iron.Business.Organisations.csproj", "Business")]
    [InlineData("src/Products/Iron.Products.Procurement/Iron.Products.Procurement.csproj", "Products")]
    [InlineData("src/Hosts/Iron.Host.Api/Iron.Host.Api.csproj", "Host")]
    [InlineData("tests/Kernel/Iron.Kernel.Identity.Tests/Iron.Kernel.Identity.Tests.csproj", "Test")]
    [InlineData("tests/Iron.Architecture.Tests/Iron.Architecture.Tests.csproj", "Test")]
    [InlineData("tools/Iron.Smoke/Iron.Smoke.csproj", "Tool")]
    public void EveryLayerAcceptsItsOwnProjects(string relativePath, string layer)
    {
        Assert.Empty(LayerRules.Check([Fixture.Declaring(relativePath, layer)]));
    }

    [Theory]
    [InlineData("src/Kernel/Iron.Kernel.Identity/Iron.Kernel.Identity.csproj", "Business")]
    [InlineData("src/Business/Iron.Business.Items/Iron.Business.Items.csproj", "Kernel")]
    [InlineData("src/Business/Iron.Kernel.Items/Iron.Kernel.Items.csproj", "Business")]
    [InlineData("src/Products/Iron.Business.Items/Iron.Business.Items.csproj", "Business")]
    [InlineData("src/Hosts/Iron.Kernel.Api/Iron.Kernel.Api.csproj", "Host")]
    [InlineData("products/Procurement/Procurement.csproj", "Products")]
    [InlineData("src/Kernel/Iron.Kernel.Identity.Tests/Iron.Kernel.Identity.Tests.csproj", "Test")]
    public void ALayerMustAgreeWithTheNameAndTheFolder(string relativePath, string layer)
    {
        var violation = Assert.Single(LayerRules.Check([Fixture.Declaring(relativePath, layer)]));

        Assert.StartsWith(relativePath, violation, StringComparison.Ordinal);
        Assert.Contains($"declares IronLayer {layer}, but a {layer} project is", violation, StringComparison.Ordinal);
    }
}
