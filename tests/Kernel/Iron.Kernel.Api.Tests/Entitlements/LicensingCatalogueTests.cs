using Iron.Kernel.Api.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api.Tests.Entitlements;

/// <summary>ADR-0034: one catalogue from the declarations, written as a file the control plane reads back unchanged.</summary>
public class LicensingCatalogueTests
{
    [Fact]
    public void TheFileFormRoundTripsAndListsCodesInOrder()
    {
        var catalogue = LicensingCatalogue.From([new Sample("zeta", new("zeta", SubscriptionType.Platform), ["zeta.b", "zeta.a"]), new Sample("alpha", null, ["alpha.only"])]);

        var json = catalogue.ToJson();
        var read = LicensingCatalogue.Parse(json);

        Assert.Equal(json, read.ToJson());
        Assert.Equal(["alpha.only", "zeta.a", "zeta.b"], read.Features.Select(f => f.Code));
        Assert.Equal(["zeta.a", "zeta.b"], Assert.Single(read.Modules).Features);
        Assert.Contains("\"subscription_type\": \"platform\"", json, StringComparison.Ordinal);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AFeatureDeclaredTwiceAndAnUnknownFormatAreRefused()
    {
        Assert.Throws<InvalidOperationException>(() => LicensingCatalogue.From([new Sample("one", null, ["shared.thing"]), new Sample("two", null, ["shared.thing"])]));
        Assert.Throws<InvalidOperationException>(() => LicensingCatalogue.Parse("""{"format_version":2,"modules":[],"features":[]}"""));
    }

    [Theory]
    [InlineData("authorization.role_transfer", true)]
    [InlineData("Authorization.RoleTransfer", false)]
    [InlineData("role_transfer", false)]
    [InlineData("a.b.c", false)]
    public void AFeatureCodeIsAreaDotFeatureInLowercase(string code, bool valid)
    {
        Assert.Equal(valid, Feature.IsValidCode(code));
    }

    private sealed class Sample(string name, ModuleLicence? licence, string[] features) : IModule
    {
        public string Name => name;

        public IReadOnlyList<Permission> Permissions => [];

        public ModuleLicence? Licence => licence;

        public IReadOnlyList<Feature> Features => [.. features.Select(code => new Feature(code, $"The {code} feature"))];

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }
}
