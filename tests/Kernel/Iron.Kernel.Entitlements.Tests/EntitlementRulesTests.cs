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
    [Proves("TF-244 AC5", "TF-244 AC6", "ADR-0059")]
    public void AnEntryFollowsItsOwnPlansGraceAndADocumentWithoutSuitesReadsAsBefore()
    {
        var suiteModule = new ModuleEntitlement("purchasing", EntitlementStatus.Enabled, SubscriptionType.Platform, null, Start, End, 30, "procurement");
        var foundationFeature = new FeatureEntitlement("authorization.role_transfer", EntitlementStatus.Enabled, Start, End, 7, SuiteName.Foundation);
        var document = new EntitlementsDocument(EntitlementsDocument.CurrentFormat, "echo", 4, "standard", 7, DateTimeOffset.UnixEpoch, [suiteModule], [foundationFeature], [new SuitePlan("procurement", "procurement-essentials", 30)]);

        var read = EntitlementsDocument.Parse(document.ToJson());

        Assert.Equal(new SuitePlan("procurement", "procurement-essentials", 30), read.FindSuite("procurement"));
        Assert.Null(read.FindSuite("school"));
        var tenDaysAfter = End.AddDays(10);
        Assert.Equal(new Entitlement(EntitlementStatus.Enabled, EntitlementReasons.InGrace), EntitlementRules.Module(read, "purchasing", tenDaysAfter));
        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.Expired), EntitlementRules.Feature(read, "authorization.role_transfer", null, tenDaysAfter));
        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.Expired), EntitlementRules.Module(read, "purchasing", End.AddDays(31)));

        // A document written before plans per suite: no suites, and every entry follows the document's grace days.
        var older = EntitlementsDocument.Parse(Documents.With(2, [Documents.Fixture(endsOn: End)], graceDays: 14).ToJson());
        Assert.Empty(older.Suites ?? []);
        Assert.Equal(EntitlementReasons.InGrace, EntitlementRules.Module(older, FixtureModule.Licence.Code, End.AddDays(14)).Reason);
        Assert.Equal(EntitlementReasons.Expired, EntitlementRules.Module(older, FixtureModule.Licence.Code, End.AddDays(15)).Reason);
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
