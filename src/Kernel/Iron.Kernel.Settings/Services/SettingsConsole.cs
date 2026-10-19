using System.Text.Json;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Settings.Contracts;

namespace Iron.Kernel.Settings.Services;

/// <summary>A unit by its id, its name and its path of names from the root ("Acme / Operations").</summary>
public sealed record UnitReference(Guid Id, string Name, string Path);

/// <summary>A person by id and display name.</summary>
public sealed record PersonReference(Guid Id, string Name);

/// <summary>Where an effective value comes from: the level, the unit for a unit's value, and who set it when.</summary>
public sealed record SettingSource(SettingLevel Level, UnitReference? Unit, PersonReference? ChangedBy, DateTimeOffset? ChangedAt);

/// <summary>
/// A setting as the console shows it at one place (TF-058): its declaration, the effective value there, where that value
/// comes from, and whether it is set at this very place (only then is there something to reset).
/// </summary>
public sealed record SettingView(
    string Key,
    string Module,
    SettingType Type,
    string Description,
    string Requirement,
    IReadOnlyList<string>? Choices,
    decimal? Minimum,
    decimal? Maximum,
    bool WholeNumber,
    IReadOnlyList<SettingLevel> Scopes,
    JsonElement Default,
    JsonElement Value,
    SettingSource Source,
    bool SetHere);

/// <summary>
/// The console's reading of settings (TF-058, ADR-0036): every setting a place may hold, by module, with the effective
/// value there and its source in names. A secret reference shows its path, never a secret (TF-057 AC3).
/// </summary>
public sealed class SettingsConsole(ISettings settings, SettingDeclarations declarations, IOrganisationUnits? units = null, IPrincipals? principals = null)
{
    private readonly Dictionary<Guid, UnitReference?> _units = [];

    public async Task<IReadOnlyList<SettingView>> ListAsync(SettingPlace place, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(place);
        var views = new List<SettingView>();
        foreach (var declared in declarations.All.Where(d => d.Definition.Allows(place.Level)))
        {
            views.Add(await ViewAsync(declared, place, cancellationToken).ConfigureAwait(false));
        }

        return views;
    }

    public async Task<SettingView> ViewAsync(DeclaredSetting declared, SettingPlace place, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(place);
        var setting = declared.Definition;
        var context = place.Level switch
        {
            SettingLevel.Unit => SettingContext.ForUnit(place.TargetId!.Value),
            SettingLevel.User => SettingContext.ForPerson(place.TargetId!.Value),
            _ => SettingContext.Tenant,
        };
        var resolved = await settings.ResolveAsync(setting, context, cancellationToken).ConfigureAwait(false);
        var unit = resolved.UnitId is { } unitId ? await UnitAsync(unitId, cancellationToken).ConfigureAwait(false) : null;
        var author = resolved.ChangedBy is { } by ? await PersonAsync(by, cancellationToken).ConfigureAwait(false) : null;
        var setHere = resolved.Level == place.Level && (place.Level != SettingLevel.Unit || resolved.UnitId == place.TargetId);
        var entry = SettingEntry.From(setting, declared.Module);
        return new SettingView(
            setting.Key,
            declared.Module,
            setting.Type,
            setting.Description,
            setting.Requirement,
            entry.Choices,
            entry.Minimum,
            entry.Maximum,
            setting.WholeNumber,
            entry.Scopes,
            setting.Default,
            resolved.Value,
            new SettingSource(resolved.Level, unit, author, resolved.ChangedAt),
            setHere);
    }

    public async Task<UnitReference?> UnitAsync(Guid unitId, CancellationToken cancellationToken)
    {
        if (_units.TryGetValue(unitId, out var known))
        {
            return known;
        }

        UnitReference? reference = null;
        if (units is not null && await units.FindAsync(unitId, cancellationToken).ConfigureAwait(false) is { } unit)
        {
            var names = new List<string>();
            foreach (var id in await units.PathAsync(unitId, cancellationToken).ConfigureAwait(false))
            {
                names.Add(id == unitId ? unit.Name : (await units.FindAsync(id, cancellationToken).ConfigureAwait(false))?.Name ?? "?");
            }

            reference = new UnitReference(unit.Id, unit.Name, string.Join(" / ", names));
        }

        _units[unitId] = reference;
        return reference;
    }

    public async Task<PersonReference?> PersonAsync(Guid principalId, CancellationToken cancellationToken)
    {
        if (principals is null)
        {
            return new PersonReference(principalId, principalId.ToString("D"));
        }

        var found = await principals.FindAsync([principalId], cancellationToken).ConfigureAwait(false);
        return found.TryGetValue(principalId, out var person) ? new PersonReference(person.Id, person.DisplayName) : new PersonReference(principalId, principalId.ToString("D"));
    }

    public async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IReadOnlyCollection<Guid> principalIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principalIds);
        if (principals is null || principalIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var found = await principals.FindAsync(principalIds, cancellationToken).ConfigureAwait(false);
        return found.ToDictionary(p => p.Key, p => p.Value.DisplayName);
    }
}
