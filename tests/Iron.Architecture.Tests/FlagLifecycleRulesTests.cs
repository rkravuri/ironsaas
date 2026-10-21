using Iron.Kernel;
using Iron.Kernel.Api.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Architecture.Tests;

/// <summary>
/// TF-052 AC2 and ADR-0035: a flag cannot leave the code while the catalogue still marks it active. Retiring a flag is an
/// explicit change of the catalogue file, in the same change that removes it from the code; the build names the flag.
/// </summary>
public class FlagLifecycleRulesTests
{
    private static readonly string CatalogueFile = Path.Combine(Repository.Root, "src", "Kernel", "Iron.Kernel.Tenancy", "Services", "licensing-catalogue.json");

    [Fact]
    [Proves("ADR-0035")]
    public void NoFlagLeftTheCodeWhileTheCatalogueMarksItActive()
    {
        var file = LicensingCatalogue.Parse(File.ReadAllText(CatalogueFile));
        var code = LicensingCatalogue.From(Iron.Host.Api.UnitModules.All());

        ArchitectureAssert.NoViolations("Flag lifecycle (TF-052 AC2, ADR-0035)", LicensingCatalogue.FlagLifecycleProblems(file, code));
        Assert.NotEmpty(file.Flags);
    }

    [Fact]
    [Proves("TF-052 AC2", "ADR-0035")]
    public void AFixtureFlagRemovedFromTheCodeIsNamedUntilTheCatalogueRetiresIt()
    {
        var flag = Flag.Boolean("fixture.bulk_edit", FlagKind.Release, "Lets a person change many things at once", "Fixture team", new DateOnly(2027, 1, 1), false);
        var before = LicensingCatalogue.From([new FixtureFlagModule(flag)]);
        var after = LicensingCatalogue.From([new FixtureFlagModule()]);

        var violation = Assert.Single(LicensingCatalogue.FlagLifecycleProblems(before, after));
        Assert.Contains("'fixture.bulk_edit' (owner: Fixture team) left the code while the catalogue marks it active", violation, StringComparison.Ordinal);

        var retired = new LicensingCatalogue(before.Modules, before.Features, before.Platform, before.Flags.Select(f => f.Key == flag.Key ? f with { Status = FlagStatus.Retired } : f));
        Assert.Empty(LicensingCatalogue.FlagLifecycleProblems(retired, after));
    }

    [Fact]
    [Proves("TF-052 AC3", "ADR-0035")]
    public void AFlagWithoutAnOwnerDoesNotBuild()
    {
        var refused = Assert.Throws<ArgumentException>(() => Flag.Boolean("fixture.ownerless", FlagKind.Operational, "Nobody's flag", string.Empty, null, false));

        Assert.Contains("declares no owner", refused.Message, StringComparison.Ordinal);
        Assert.All(LicensingCatalogue.From(Iron.Host.Api.UnitModules.All()).Flags, f => Assert.False(string.IsNullOrWhiteSpace(f.Owner)));
    }

    private sealed class FixtureFlagModule(params Flag[] flags) : IModule
    {
        public string Name => "fixture";

        public IReadOnlyList<Permission> Permissions => [];

        public IReadOnlyList<Flag> Flags => flags;

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }
}
