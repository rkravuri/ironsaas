using System.Text.Json;
using Iron.Kernel.Flags.Contracts;
using Iron.Kernel.Flags.Services;

namespace Iron.Kernel.Flags.Tests;

/// <summary>
/// ADR-0035 on rules alone: the declarations, the stable hash (TF-050 AC1), the evaluation order (TF-050 AC2: off globally,
/// on for a tenant, on for that tenant), attribute match, and a value of the wrong type skipped.
/// </summary>
public class FlagRulesTests
{
    private static readonly Flag Release = ExampleFlags.Release;
    private static readonly Guid Root = Guid.Parse("01a15100-0000-7000-8000-000000000001");
    private static readonly Guid Operations = Guid.Parse("01a15100-0000-7000-8000-000000000003");
    private static readonly Guid North = Guid.Parse("01a15100-0000-7000-8000-000000000004");
    private static readonly Guid Ada = Guid.Parse("01a14e12-0000-7000-8000-000000000001");

    private static FlagSubject Subject(string tenant = "echo", string? region = "eu", string? plan = "enterprise", Guid? principal = null, string? type = "user") =>
        new(tenant, region, plan, principal ?? Ada, type, [North, Operations, Root]);

    [Theory]
    [InlineData("flags.example_release", "acme", 3038)]
    [InlineData("flags.example_release", "bravo", 1133)]
    [InlineData("flags.example_release", "echo", 8608)]
    [InlineData("flags.example_tone", "acme", 1585)]
    [InlineData("kill.imports", "01a14e12-0000-7000-8000-000000000001", 639)]
    public void TheStableHashGivesTheSameBucketOnEveryNodeAndInEveryRelease(string flag, string subject, int bucket)
    {
        // Computed once with another implementation of SHA-256: a change of the algorithm moves tenants between groups.
        Assert.Equal(bucket, StableHash.Bucket(flag, subject));
        Assert.Equal(bucket, StableHash.Bucket(flag, subject));
    }

    [Fact]
    public void TenPerCentOfTenThousandTenantsIsAboutATenthAndTheSameTenantAlwaysLandsTheSameWay()
    {
        var rule = Rules.Global(Release.Key, true, condition: FlagCondition.PercentageOf(10, RolloutSubject.Tenant));
        var on = Enumerable.Range(0, 10_000).Count(i => FlagRules.Evaluate(Release, [rule], Subject(tenant: $"tenant-{i}")).Value.GetBoolean());

        Assert.InRange(on, 900, 1100);
        var first = FlagRules.Evaluate(Release, [rule], Subject(tenant: "acme")).Value.GetBoolean();
        Assert.All(Enumerable.Range(0, 50), _ => Assert.Equal(first, FlagRules.Evaluate(Release, [rule], Subject(tenant: "acme")).Value.GetBoolean()));
        // Another flag's rollout does not pick the same tenants.
        Assert.NotEqual(StableHash.Bucket("flags.example_release", "acme"), StableHash.Bucket("flags.example_tone", "acme"));
    }

    [Fact]
    public void OffGloballyButOnForATenantIsOnForThatTenantAndOffForTheOthers()
    {
        IReadOnlyList<FlagRuleEntry> rules = [Rules.Global(Release.Key, false), Rules.Tenant(Release.Key, "echo", true)];

        Assert.True(FlagRules.Evaluate(Release, rules, Subject("echo")).Value.GetBoolean());
        Assert.False(FlagRules.Evaluate(Release, rules, Subject("acme")).Value.GetBoolean());
    }

    [Fact]
    public void TheMostSpecificScopeWinsUserThenUnitThenTenantThenRegionThenGlobal()
    {
        var tone = ExampleFlags.Tone;
        var global = Rules.Global(tone.Key, "global");
        var region = Rules.Region(tone.Key, "eu", "region");
        var tenant = Rules.Tenant(tone.Key, "echo", "tenant");
        var unit = Rules.Unit(tone.Key, "echo", Operations, "unit");
        var user = Rules.User(tone.Key, "echo", Ada, "user");

        Assert.Equal("user", Value(tone, [global, region, tenant, unit, user]));
        Assert.Equal("unit", Value(tone, [global, region, tenant, unit]));
        Assert.Equal("tenant", Value(tone, [global, region, tenant]));
        Assert.Equal("region", Value(tone, [global, region]));
        Assert.Equal("global", Value(tone, [global]));
        Assert.Equal("plain", Value(tone, []));
        Assert.Equal("global", Value(tone, [global, region], Subject(region: "us")));
    }

    [Fact]
    public void AUnitRuleReachesTheUnitsBelowItAndTheNearestUnitWins()
    {
        var tone = ExampleFlags.Tone;
        var root = Rules.Unit(tone.Key, "echo", Root, "root");
        var operations = Rules.Unit(tone.Key, "echo", Operations, "operations");

        Assert.Equal("operations", Value(tone, [root, operations]));
        Assert.Equal("root", Value(tone, [root]));
        // A unit of another tenant, or a principal without units, does not match.
        Assert.Equal("plain", Value(tone, [Rules.Unit(tone.Key, "acme", Operations, "elsewhere")]));
        Assert.Equal("plain", Value(tone, [root], Subject() with { Units = [] }));
    }

    [Fact]
    public void WithinAScopeTheOrderDecidesAndAConditionThatFailsPassesToTheNextRule()
    {
        var tone = ExampleFlags.Tone;
        var services = Rules.Tenant(tone.Key, "echo", "for services", position: 0, condition: FlagCondition.AttributeIn(FlagAttributes.PrincipalType, "service"));
        var people = Rules.Tenant(tone.Key, "echo", "for people", position: 1);

        Assert.Equal("for people", Value(tone, [people, services]));
        Assert.Equal("for services", Value(tone, [people, services], Subject(type: "service")));
        var standard = Rules.Global(tone.Key, "standard plan", condition: FlagCondition.AttributeIn(FlagAttributes.Plan, "standard"));
        Assert.Equal("plain", Value(tone, [standard]));
        Assert.Equal("standard plan", Value(tone, [standard], Subject(plan: "standard")));
    }

    [Fact]
    public void AValueOfTheWrongTypeIsSkippedAndTheResultSaysWhy()
    {
        var wrong = Rules.Tenant(Release.Key, "echo", "yes");
        var right = Rules.Global(Release.Key, true);

        var result = FlagRules.Evaluate(Release, [wrong, right], Subject());

        Assert.True(result.Value.GetBoolean());
        Assert.Equal(right, result.Rule);
        Assert.Equal(FlagResult.TargetingReason, result.Reason);
        Assert.Equal(FlagResult.DefaultReason, FlagRules.Evaluate(Release, [], Subject()).Reason);
        Assert.Equal(FlagResult.SplitReason, FlagRules.Evaluate(Release, [Rules.Global(Release.Key, true, condition: FlagCondition.PercentageOf(100, RolloutSubject.User))], Subject()).Reason);
    }

    [Fact]
    public void AKillSwitchIsOnWhereverAnyApplicableRuleSaysSoAndATenantRuleCannotExemptATenant()
    {
        var imports = KillSwitches.Imports;
        // Rules that say false for the tenant, a unit and a person, as one stored before the control plane refused them.
        List<FlagRuleEntry> rules =
        [
            Rules.Global(imports.Key, true, reason: "Imports wait for a fix."),
            Rules.Tenant(imports.Key, "echo", false),
            Rules.Unit(imports.Key, "echo", North, false),
            Rules.User(imports.Key, "echo", Ada, false),
        ];

        var result = FlagRules.Evaluate(imports, rules, Subject());
        Assert.True(result.Value.GetBoolean());
        Assert.Equal("Imports wait for a fix.", result.Rule!.Reason);

        // Without the global rule the false ones turn nothing on, and a tenant's own switch stops that tenant alone.
        Assert.False(FlagRules.Evaluate(imports, [.. rules.Skip(1)], Subject()).Value.GetBoolean());
        List<FlagRuleEntry> acmeOnly = [Rules.Tenant(imports.Key, "acme", true, reason: "Acme's import is broken.")];
        Assert.False(FlagRules.Evaluate(imports, acmeOnly, Subject()).Value.GetBoolean());
        Assert.True(FlagRules.Evaluate(imports, acmeOnly, Subject(tenant: "acme")).Value.GetBoolean());

        // An ordinary flag keeps the most specific rule: off for the tenant wins over on for the platform.
        Assert.False(FlagRules.Evaluate(Release, [Rules.Global(Release.Key, true), Rules.Tenant(Release.Key, "echo", false)], Subject()).Value.GetBoolean());
    }

    [Fact]
    public void ADeclarationNeedsAnOwnerAPurposeARemovalDateForAReleaseAndADefaultOfItsType()
    {
        Assert.Contains("declares no owner", Assert.Throws<ArgumentException>(() => Flag.Boolean("sample.thing", FlagKind.Operational, "Does a thing", " ", null, false)).Message, StringComparison.Ordinal);
        Assert.Contains("no removal date", Assert.Throws<ArgumentException>(() => Flag.Boolean("sample.thing", FlagKind.Release, "Does a thing", "Team", null, false)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new Flag("sample.thing", FlagType.Number, FlagKind.Operational, "Does a thing", "Team", null, JsonSerializer.SerializeToElement("three")));
        Assert.Contains("kill-switch family", Assert.Throws<ArgumentException>(() => Flag.String("kill.something", FlagKind.Operational, "Not a switch", "Team", null, "on")).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Flag.Boolean("NotAKey", FlagKind.Operational, "Does a thing", "Team", null, false));
        Assert.Equal(["kill.ai", "kill.email", "kill.imports", "kill.push", "kill.sms", "kill.webhooks"], KillSwitches.All.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.All(KillSwitches.All, flag => Assert.Equal((FlagType.Boolean, FlagKind.Operational, false), (flag.Type, flag.Kind, flag.Default.GetBoolean())));
        Assert.Equal("imports", KillSwitches.Imports.KillSwitchPath);
    }

    [Fact]
    public void TheEvaluationContextCarriesTenantRegionPlanPrincipalTypeAndUnitsAndReadsBackTheSame()
    {
        var subject = Subject(type: "service");

        var context = FlagSubjects.ContextOf(subject);
        var back = IronFlagProvider.SubjectOf(context);

        Assert.Equal(Ada.ToString("D"), context.TargetingKey);
        Assert.Equal(North.ToString("D"), context.GetValue(FlagContextKeys.HomeUnit).AsString);
        Assert.Equal((subject.Tenant, subject.Region, subject.Plan, subject.PrincipalId, subject.PrincipalType), (back.Tenant, back.Region, back.Plan, back.PrincipalId, back.PrincipalType));
        Assert.Equal(subject.Units, back.Units);
    }

    private static string? Value(Flag flag, IReadOnlyList<FlagRuleEntry> rules, FlagSubject? subject = null) =>
        FlagRules.Evaluate(flag, rules, subject ?? Subject()).Value.GetString();
}
