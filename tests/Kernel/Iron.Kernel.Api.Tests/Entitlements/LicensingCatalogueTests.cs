using System.Text.Json;
using Iron.Kernel.Api.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api.Tests.Entitlements;

/// <summary>ADR-0034: one catalogue from the declarations, written as a file the control plane reads back unchanged, grouped by suite (ADR-0043).</summary>
public class LicensingCatalogueTests
{
    [Fact]
    [Proves("ADR-0034")]
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
    [Proves("ADR-0043")]
    public void AFeatureDeclaredTwiceAndAnUnknownFormatAreRefused()
    {
        Assert.Throws<InvalidOperationException>(() => LicensingCatalogue.From([new Sample("one", null, ["shared.thing"]), new Sample("two", null, ["shared.thing"])]));
        // Version 1 is the flat file of the builds before the suites; this build reads version 2 only.
        Assert.Throws<InvalidOperationException>(() => LicensingCatalogue.Parse("""{"format_version":1,"modules":[],"features":[]}"""));
    }

    [Fact]
    [Proves("ADR-0034", "ADR-0043")]
    public void TheFileGroupsBySuiteWithTheFoundationFirst()
    {
        var catalogue = LicensingCatalogue.From(
        [
            new Sample("pupils", new("pupils", SubscriptionType.PerPerson), [], "school"),
            new Sample("orders", new("orders", SubscriptionType.PerTransaction), ["orders.blanket"], "procurement"),
            new Sample("items", new("items", SubscriptionType.Platform), ["items.variants"]),
            new Sample("audit", null, []),
        ]);

        Assert.Equal(["foundation", "procurement", "school"], catalogue.Suites);
        using var file = JsonDocument.Parse(catalogue.ToJson());
        var groups = file.RootElement.GetProperty("suites").EnumerateArray().ToList();
        Assert.Equal(["foundation", "procurement", "school"], groups.Select(g => g.GetProperty("suite").GetString()));
        Assert.Equal(["items"], Codes(groups[0], "modules"));
        Assert.Equal(["items.variants"], Codes(groups[0], "features"));
        Assert.Equal(["orders"], Codes(groups[1], "modules"));
        Assert.Equal(["orders.blanket"], Codes(groups[1], "features"));
        Assert.Equal(["pupils"], Codes(groups[2], "modules"));
        // The kill switches are the kernel's, so the Foundation's; a suite here declares no flag.
        Assert.Equal(KillSwitches.All.Count, groups[0].GetProperty("flags").GetArrayLength());
        Assert.Equal(0, groups[1].GetProperty("flags").GetArrayLength());
        Assert.Equal(["audit"], file.RootElement.GetProperty("platform").EnumerateArray().Select(p => p.GetString()));

        var read = LicensingCatalogue.Parse(catalogue.ToJson());
        Assert.Equal("procurement", read.FindModule("orders")!.Suite);
        Assert.Equal("procurement", read.FindFeature("orders.blanket")!.Suite);
        Assert.Equal(SuiteName.Foundation, read.FindModule("items")!.Suite);
        Assert.Equal(["items", "orders", "pupils"], read.Modules.Select(m => m.Code));
        Assert.Equal(catalogue.ToJson(), read.ToJson());
    }

    [Fact]
    [Proves("ADR-0043")]
    public void AnUnlicensedSuiteModuleAWrongSuiteAndAGroupNamedTwiceAreRefused()
    {
        Assert.Contains("declares no licence", Assert.Throws<InvalidOperationException>(() => LicensingCatalogue.From([new Sample("orders", null, [], "procurement")])).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => LicensingCatalogue.From([new Sample("orders", new("orders", SubscriptionType.PerPerson), [], "Procurement")]));
        Assert.Throws<InvalidOperationException>(() => LicensingCatalogue.Parse("""{"format_version":2,"suites":[{"suite":"foundation"},{"suite":"foundation"}]}"""));
        Assert.Throws<InvalidOperationException>(() => LicensingCatalogue.Parse("""{"format_version":2,"suites":[{"suite":"kernel"}]}"""));
    }

    [Theory]
    [Proves("ADR-0034")]
    [InlineData("authorization.role_transfer", true)]
    [InlineData("Authorization.RoleTransfer", false)]
    [InlineData("role_transfer", false)]
    [InlineData("a.b.c", false)]
    public void AFeatureCodeIsAreaDotFeatureInLowercase(string code, bool valid)
    {
        Assert.Equal(valid, Feature.IsValidCode(code));
    }

    [Fact]
    [Proves("ADR-0035")]
    public void TheCatalogueCarriesTheKillSwitchesAndEachModulesFlagsWithTheirDeclarations()
    {
        var catalogue = LicensingCatalogue.From([new Flagged("tone", Flag.String("tone.style", FlagKind.Experiment, "Tries a warmer tone", "Writers", new DateOnly(2027, 1, 1), "plain"))]);

        Assert.Equal(KillSwitches.All.Count + 1, catalogue.Flags.Count);
        Assert.All(catalogue.Flags.Where(f => f.IsKillSwitch), f => Assert.Equal(LicensingCatalogue.KernelModule, f.Module));
        var tone = catalogue.FindActiveFlag("tone.style")!;
        Assert.Equal(("tone", FlagType.String, FlagKind.Experiment, "Writers", new DateOnly(2027, 1, 1), "plain"), (tone.Module, tone.Type, tone.Kind, tone.Owner, tone.RemoveBy!.Value, tone.Default.GetString()));
        Assert.Equal(catalogue.ToJson(), LicensingCatalogue.Parse(catalogue.ToJson()).ToJson());
        Assert.Throws<InvalidOperationException>(() => LicensingCatalogue.From([new Flagged("rogue", KillSwitches.Imports)]));
    }

    [Fact]
    [Proves("TF-052 AC2", "ADR-0035")]
    public void AFlagThatLeftTheCodeWhileActiveIsNamedAndARetiredOneIsKept()
    {
        var tone = Flag.String("tone.style", FlagKind.Experiment, "Tries a warmer tone", "Writers", new DateOnly(2027, 1, 1), "plain");
        var withFlag = LicensingCatalogue.From([new Flagged("tone", tone)]);
        var withoutFlag = LicensingCatalogue.From([new Flagged("tone")]);

        var problem = Assert.Single(LicensingCatalogue.FlagLifecycleProblems(withFlag, withoutFlag));
        Assert.Contains("The flag 'tone.style' (owner: Writers) left the code while the catalogue marks it active", problem, StringComparison.Ordinal);

        // Retiring is a change of the file: the same entry, marked retired, read back from its JSON.
        var retired = withFlag.Flags.Select(f => f.Key == "tone.style" ? f with { Status = FlagStatus.Retired } : f);
        var retiredFile = LicensingCatalogue.Parse(new LicensingCatalogue(withFlag.Modules, withFlag.Features, withFlag.Platform, retired).ToJson());
        Assert.Contains("\"status\": \"retired\"", retiredFile.ToJson(), StringComparison.Ordinal);
        Assert.Equal(FlagStatus.Retired, retiredFile.FindFlag("tone.style")!.Status);
        Assert.Empty(LicensingCatalogue.FlagLifecycleProblems(retiredFile, withoutFlag));
        var kept = withoutFlag.WithRetiredFrom(retiredFile);
        Assert.Equal(FlagStatus.Retired, kept.FindFlag("tone.style")!.Status);
        Assert.Null(kept.FindActiveFlag("tone.style"));
        Assert.Contains("still declares it", Assert.Single(LicensingCatalogue.FlagLifecycleProblems(retiredFile, withFlag)), StringComparison.Ordinal);
    }

    private sealed class Flagged(string name, params Flag[] flags) : IModule
    {
        public string Name => name;

        public IReadOnlyList<Permission> Permissions => [];

        public IReadOnlyList<Flag> Flags => flags;

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }

    private static IEnumerable<string?> Codes(JsonElement group, string list) => group.GetProperty(list).EnumerateArray().Select(entry => entry.GetProperty("code").GetString());

    /// <summary>A module of a test assembly is the Foundation's unless it says otherwise; a real module never does (ADR-0043).</summary>
    private sealed class Sample(string name, ModuleLicence? licence, string[] features, string suite = SuiteName.Foundation) : IModule
    {
        public string Name => name;

        public string Suite => suite;

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
