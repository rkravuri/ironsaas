using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Entitlements.Services;

namespace Iron.Kernel.Entitlements.Tests;

/// <summary>TF-040 AC3 and TF-046 AC3 on dates alone: expiry, grace, a start in the future, and a feature governed by its module.</summary>
public class EntitlementRulesTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly End = new(2026, 6, 30);

    [Theory]
    [Proves("TF-040 AC3", "ADR-0034")]
    [InlineData("2025-12-31", EntitlementStatus.Disabled, EntitlementReasons.NotStarted)]
    [InlineData("2026-01-01", EntitlementStatus.Enabled, EntitlementReasons.Licensed)]
    [InlineData("2026-06-30", EntitlementStatus.Enabled, EntitlementReasons.Licensed)]
    [InlineData("2026-07-01", EntitlementStatus.Enabled, EntitlementReasons.InGrace)]
    [InlineData("2026-07-14", EntitlementStatus.Enabled, EntitlementReasons.InGrace)]
    [InlineData("2026-07-15", EntitlementStatus.Disabled, EntitlementReasons.Expired)]
    public void ASubscriptionRunsToItsEndDateThenThroughItsGraceThenExpires(string today, EntitlementStatus status, string reason)
    {
        var entitlement = EntitlementRules.Evaluate(EntitlementStatus.Enabled, Start, End, graceDays: 14, DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(new Entitlement(status, reason), entitlement);
    }

    [Fact]
    [Proves("TF-040 AC3", "ADR-0034")]
    public void ReadOnlyStaysReadOnlyInGraceAndASwitchedOffEntitlementIsDisabledWithItsReason()
    {
        Assert.Equal(new Entitlement(EntitlementStatus.ReadOnly, EntitlementReasons.InGrace), EntitlementRules.Evaluate(EntitlementStatus.ReadOnly, Start, End, 14, End.AddDays(1)));
        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.SwitchedOff), EntitlementRules.Evaluate(EntitlementStatus.Disabled, Start, null, 14, End));
        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.Expired), EntitlementRules.Evaluate(EntitlementStatus.Enabled, Start, End, 0, End.AddDays(1)));
    }

    [Fact]
    [Proves("TF-046 AC3")]
    public void AFeatureOfAModuleThatIsNotAvailableIsUnavailableWhateverItsOwnStatus()
    {
        var document = Documents.With(1, [Documents.Fixture(EntitlementStatus.Disabled)], [Documents.Feature(FixtureModule.Bulk.Code)]);
        var module = EntitlementRules.Module(document, FixtureModule.Licence.Code, End);

        var feature = EntitlementRules.Feature(document, FixtureModule.Bulk.Code, module, End);

        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.SwitchedOff), module);
        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.ModuleUnavailable), feature);
    }

    [Fact]
    [Proves("ADR-0034")]
    public void AFeatureOfAReadOnlyModuleIsReadOnlyAndAFeatureOutsideThePlanIsNotLicensed()
    {
        var document = Documents.With(1, [Documents.Fixture(EntitlementStatus.ReadOnly)], [Documents.Feature(FixtureModule.Bulk.Code)]);
        var module = EntitlementRules.Module(document, FixtureModule.Licence.Code, End);

        Assert.Equal(EntitlementStatus.ReadOnly, EntitlementRules.Feature(document, FixtureModule.Bulk.Code, module, End).Status);
        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.NotLicensed), EntitlementRules.Feature(document, "authorization.role_transfer", null, End));
        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.NotLicensed), EntitlementRules.Module(Documents.With(1), FixtureModule.Licence.Code, End));
    }

    [Fact]
    [Proves("ADR-0034")]
    public void TheDocumentRoundTripsAndAFormatThisBuildDoesNotKnowIsRefused()
    {
        var document = Documents.With(7, [Documents.Fixture(endsOn: End)], [Documents.Feature("authorization.role_transfer")]);

        var read = EntitlementsDocument.Parse(document.ToJson());

        Assert.Equal(7, read.Revision);
        Assert.Equal(End, read.FindModule("fixture")!.EndsOn);
        Assert.Equal(SubscriptionType.PerPerson, read.FindModule("fixture")!.SubscriptionType);
        Assert.Contains("\"subscription_type\":\"per_person\"", document.ToJson(), StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => EntitlementsDocument.Parse(document.ToJson().Replace("\"format_version\":1", "\"format_version\":2", StringComparison.Ordinal)));
        Assert.Equal("tenants/echo/entitlements", EntitlementsDocument.PathOf("echo"));
    }
}
