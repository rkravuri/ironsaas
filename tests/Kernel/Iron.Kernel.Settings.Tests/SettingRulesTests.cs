using System.Text.Json;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Settings.Contracts;
using Iron.Kernel.Settings.Services;

namespace Iron.Kernel.Settings.Tests;

/// <summary>
/// ADR-0036 on declarations and values alone: what a declaration must say, a value refused with the rule in words
/// (TF-057 AC2), the scopes a setting allows, and the resolution order with a unit two levels up (TF-057 AC1, TF-221).
/// </summary>
public class SettingRulesTests
{
    private static readonly DateTimeOffset Changed = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Person = Guid.Parse("01a15100-0000-7000-8000-0000000000a1");
    private static readonly Guid Root = Guid.Parse("01a15100-0000-7000-8000-0000000000b1");
    private static readonly Guid Operations = Guid.Parse("01a15100-0000-7000-8000-0000000000b2");
    private static readonly Guid Warehouse = Guid.Parse("01a15100-0000-7000-8000-0000000000b3");

    [Theory]
    [Proves("TF-057 AC2", "ADR-0036")]
    [InlineData("10", "A whole number from 15 to 480.")]
    [InlineData("481", "A whole number from 15 to 480.")]
    [InlineData("30.5", "A whole number from 15 to 480.")]
    [InlineData("\"thirty\"", "A whole number from 15 to 480.")]
    [InlineData("null", "A whole number from 15 to 480.")]
    public void AValueOutsideTheRuleIsRefusedWithTheRuleInWords(string json, string words) =>
        Assert.Equal(words, IdentitySettings.SessionIdleMinutes.Problem(Json(json)));

    [Theory]
    [Proves("ADR-0036")]
    [InlineData("15")]
    [InlineData("480")]
    [InlineData("30")]
    public void AValueInsideTheRuleIsAccepted(string json) => Assert.Null(IdentitySettings.SessionIdleMinutes.Problem(Json(json)));

    [Fact]
    [Proves("TF-057 AC2", "ADR-0036")]
    public void AChoiceNamesItsChoicesATextItsLengthAndASecretReferenceAPath()
    {
        Assert.Equal("One of: light, dark, auto.", ShellSettings.ColourScheme.Problem(Json("\"purple\"")));
        Assert.Null(ShellSettings.ColourScheme.Problem(Json("\"dark\"")));

        var signature = Setting.Text("probe.mail.signature", "The line under every mail.", "Kind regards", SettingScopes.Tenant, SettingRule.Length(2, 40));
        Assert.Equal("From 2 to 40 characters.", signature.Problem(Json("\"x\"")));

        Assert.Equal("A path in the secret store, for example tenants/acme/settings/mail.", ProbeSettings.MailPassword.Problem(Json("\"The secret itself\"")));
        Assert.Null(ProbeSettings.MailPassword.Problem(Json("\"tenants/echo/settings/mail\"")));
    }

    [Fact]
    [Proves("ADR-0036")]
    public void ADeclarationSaysWhatItIsAndItsDefaultKeepsItsOwnRule()
    {
        Assert.Contains("declares no description", Assert.Throws<ArgumentException>(() => Setting.Boolean("probe.quiet", " ", false, SettingScopes.Tenant)).Message, StringComparison.Ordinal);
        Assert.Contains("allows no scope", Assert.Throws<ArgumentException>(() => Setting.Boolean("probe.quiet", "Quiet.", false, SettingScopes.None)).Message, StringComparison.Ordinal);
        Assert.Contains("is not a setting key", Assert.Throws<ArgumentException>(() => Setting.Boolean("Quiet", "Quiet.", false, SettingScopes.Tenant)).Message, StringComparison.Ordinal);
        Assert.Contains("breaks its own rule: A whole number from 1 to 10.", Assert.Throws<ArgumentException>(() => Setting.WholeNumber("probe.size", "Size.", 11, 1, 10, SettingScopes.Tenant)).Message, StringComparison.Ordinal);
        Assert.Contains("breaks its own rule: One of: a, b.", Assert.Throws<ArgumentException>(() => Setting.Choice("probe.pick", "Pick.", "c", ["a", "b"], SettingScopes.User)).Message, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0036")]
    public void ASettingAllowsOnlyTheScopesItDeclares()
    {
        Assert.True(IdentitySettings.SessionIdleMinutes.Allows(SettingLevel.Tenant));
        Assert.False(IdentitySettings.SessionIdleMinutes.Allows(SettingLevel.Unit));
        Assert.False(IdentitySettings.SessionIdleMinutes.Allows(SettingLevel.User));
        Assert.True(IdentitySettings.InvitationValidDays.Allows(SettingLevel.Unit));
        Assert.False(ShellSettings.Density.Allows(SettingLevel.Tenant));
        Assert.True(ShellSettings.Density.Allows(SettingLevel.User));
    }

    [Fact]
    [Proves("ADR-0036")]
    public void ATypedDefinitionReadsItsValueAsItsType()
    {
        Assert.Equal(60, IdentitySettings.SessionIdleMinutes.DefaultValue);
        Assert.Equal(30, IdentitySettings.SessionIdleMinutes.Read(Json("30")));
        Assert.Equal("auto", ShellSettings.ColourScheme.DefaultValue);
        Assert.Null(ProbeSettings.MailPassword.DefaultValue);
        Assert.Equal("tenants/echo/settings/mail", ProbeSettings.MailPassword.Read(Json("\"tenants/echo/settings/mail\""))!.Path);
    }

    [Fact]
    [Proves("TF-057 AC1", "ADR-0036")]
    public void TheOrderIsThePersonThenTheUnitsFromTheNearestUpThenTheTenantThenTheDefault()
    {
        var path = new[] { Warehouse, Operations, Root };
        List<StoredValue> values = [Value(SettingLevel.Tenant, Guid.Empty, "warm")];

        Assert.Equal(("warm", SettingLevel.Tenant), Effective(values, path));

        // A unit two levels up: the warehouse sits in Operations, which sits in the root.
        values.Add(Value(SettingLevel.Unit, Root, "brief"));
        var resolved = SettingResolution.Resolve(ProbeSettings.Tone, values, Person, path);
        Assert.Equal(("brief", SettingLevel.Unit, Root), (resolved.Value.GetString(), resolved.Level, resolved.UnitId));

        // The nearer unit wins over the one above it.
        values.Add(Value(SettingLevel.Unit, Operations, "plain"));
        Assert.Equal(("plain", SettingLevel.Unit), Effective(values, path));

        // The person's own value wins over every unit.
        values.Add(Value(SettingLevel.User, Person, "warm"));
        Assert.Equal(("warm", SettingLevel.User), Effective(values, path));

        // Someone else's own value counts for nobody else.
        Assert.Equal(("plain", SettingLevel.Unit), Effective(values, path, Guid.Parse("01a15100-0000-7000-8000-0000000000a2")));
        Assert.Equal(("plain", SettingLevel.Default), Effective([], path));
    }

    [Fact]
    [Proves("ADR-0036")]
    public void ALevelTheDeclarationDoesNotAllowAndAValueItsRuleNoLongerAcceptsAreSkipped()
    {
        List<StoredValue> values =
        [
            new(IdentitySettings.SessionIdleMinutes.Key, SettingLevel.Tenant, Guid.Empty, Json("45"), Person, Changed),
            new(IdentitySettings.SessionIdleMinutes.Key, SettingLevel.Unit, Operations, Json("20"), Person, Changed),
            new(IdentitySettings.SessionIdleMinutes.Key, SettingLevel.User, Person, Json("30"), Person, Changed),
        ];
        Assert.Equal(45, IdentitySettings.SessionIdleMinutes.Read(SettingResolution.Resolve(IdentitySettings.SessionIdleMinutes, values, Person, [Operations]).Value));

        // A rule tightened after the value was saved: the bad value is not served; the default is.
        List<StoredValue> stale = [new(IdentitySettings.SessionIdleMinutes.Key, SettingLevel.Tenant, Guid.Empty, Json("5"), Person, Changed)];
        Assert.Equal(SettingLevel.Default, SettingResolution.Resolve(IdentitySettings.SessionIdleMinutes, stale, null, []).Level);
    }

    [Fact]
    [Proves("ADR-0036")]
    public void TheCatalogueListsEverySettingWithItsModuleAndTheShellGetsTypedKeysForTheVisibleOnes()
    {
        var catalogue = LicensingCatalogue.From([new SettingsModule(), new ProbeModule()]);
        var tone = Assert.Single(catalogue.Settings, s => s.Key == ProbeSettings.Tone.Key);
        Assert.Equal(("probe", SettingType.Choice, "One of: plain, warm, brief."), (tone.Module, tone.Type, tone.Requirement));
        Assert.Equal([SettingLevel.Tenant, SettingLevel.Unit, SettingLevel.User], tone.Scopes);

        var keys = ShellSettingKeys.ToTypeScript(catalogue);
        Assert.Contains("  \"probe.reply.tone\": \"plain\" | \"warm\" | \"brief\";\n", keys, StringComparison.Ordinal);
        Assert.Contains("  \"shell.density\": \"comfortable\",\n", keys, StringComparison.Ordinal);
        // Neither a secret reference nor a setting the shell may not see is in the file.
        Assert.DoesNotContain("probe.mail.password", keys, StringComparison.Ordinal);
        Assert.DoesNotContain("probe.limits.batch", keys, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0036")]
    public void TwoModulesCannotDeclareOneKey() =>
        Assert.Contains("is declared by the modules 'probe' and 'probe'", Assert.Throws<InvalidOperationException>(() => new SettingDeclarations([new ProbeModule(), new ProbeModule()])).Message, StringComparison.Ordinal);

    private static (string?, SettingLevel) Effective(IReadOnlyList<StoredValue> values, IReadOnlyList<Guid> path, Guid? person = null)
    {
        var resolved = SettingResolution.Resolve(ProbeSettings.Tone, values, person ?? Person, path);
        return (resolved.Value.GetString(), resolved.Level);
    }

    private static StoredValue Value(SettingLevel level, Guid target, string tone) => new(ProbeSettings.Tone.Key, level, target, Json($"\"{tone}\""), Person, Changed);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
