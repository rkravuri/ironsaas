using System.Text.Json;
using Iron.Kernel.Api.Context;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Settings.Contracts;

namespace Iron.Kernel.Settings.Api;

/// <summary>A setting's effective value for the signed-in person, and the level it comes from.</summary>
public sealed record ContextSettingValue(string Key, JsonElement Value, SettingLevel Level);

/// <summary>The <c>settings</c> part of <c>/me/context</c>.</summary>
public sealed record ContextSettings(IReadOnlyList<ContextSettingValue> Values);

/// <summary>
/// The <c>settings</c> part of <c>GET /api/v1/me/context</c> (TF-059, ADR-0036): the effective value, for this person,
/// of every setting its declaration lets the shell see. A secret reference is never among them.
/// </summary>
public sealed class SettingsContextPart(ISettings settings, SettingDeclarations declarations) : IContextPart
{
    public const string PartName = "settings";

    public string Name => PartName;

    public async Task<object> BuildAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var values = new List<ContextSettingValue>();
        var person = SettingContext.ForPerson(principalId);
        foreach (var declared in declarations.All.Where(d => d.Definition.VisibleToShell && !d.Definition.IsSecret))
        {
            var resolved = await settings.ResolveAsync(declared.Definition, person, cancellationToken).ConfigureAwait(false);
            values.Add(new ContextSettingValue(resolved.Key, resolved.Value, resolved.Level));
        }

        return new ContextSettings(values);
    }
}
