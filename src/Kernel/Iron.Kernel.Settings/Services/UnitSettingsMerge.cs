using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Settings.Contracts;
using Iron.Kernel.Settings.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Settings.Services;

/// <summary>
/// Settings' part of a merge (TF-223, ADR-0038): each value set at the unit that goes moves to the unit that stays,
/// unless that unit has its own value of the same setting, which wins; the value that goes is dropped and listed as a
/// conflict. Both are written to the history as a reset at one unit and a set at the other, so the history of either
/// unit reads true. The merge's own audit event lists them.
/// </summary>
public sealed class UnitSettingsMerge(SettingsDbContext context, SettingsRefresher refresher) : IUnitMergeParticipant
{
    public async Task<IReadOnlyList<UnitMergePart>> PreviewAsync(Guid fromUnitId, Guid intoUnitId, CancellationToken cancellationToken)
    {
        var (moving, dropping) = await SplitAsync(context.Values.AsNoTracking(), fromUnitId, intoUnitId, cancellationToken).ConfigureAwait(false);
        return [Part(moving, dropping)];
    }

    public async Task<IReadOnlyList<UnitMergePart>> ApplyAsync(Guid fromUnitId, Guid intoUnitId, CancellationToken cancellationToken)
    {
        var (moving, dropping) = await SplitAsync(context.Values, fromUnitId, intoUnitId, cancellationToken).ConfigureAwait(false);
        foreach (var value in moving)
        {
            context.History.Add(new SettingChange(value.Key, SettingLevel.Unit, fromUnitId, SettingChange.ResetKind, value.ValueJson, null));
            context.History.Add(new SettingChange(value.Key, SettingLevel.Unit, intoUnitId, SettingChange.SetKind, null, value.ValueJson));
            value.MoveToUnit(intoUnitId);
        }

        foreach (var value in dropping)
        {
            context.History.Add(new SettingChange(value.Key, SettingLevel.Unit, fromUnitId, SettingChange.ResetKind, value.ValueJson, null));
        }

        context.Values.RemoveRange(dropping);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return [Part(moving, dropping)];
    }

    /// <summary>The moved values show on the next request in this process, not after the refresh interval.</summary>
    public Task MergedAsync(CancellationToken cancellationToken) => refresher.RefreshOnceAsync(cancellationToken);

    private static async Task<(List<SettingValue> Moving, List<SettingValue> Dropping)> SplitAsync(IQueryable<SettingValue> values, Guid fromUnitId, Guid intoUnitId, CancellationToken cancellationToken)
    {
        var atFrom = await values.Where(v => v.Level == SettingLevel.Unit && v.TargetId == fromUnitId).OrderBy(v => v.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
        var keysAtInto = await values.Where(v => v.Level == SettingLevel.Unit && v.TargetId == intoUnitId).Select(v => v.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
        var dropping = atFrom.Where(v => keysAtInto.Contains(v.Key)).ToList();
        return ([.. atFrom.Except(dropping)], dropping);
    }

    private static UnitMergePart Part(IReadOnlyList<SettingValue> moving, IReadOnlyList<SettingValue> dropping) =>
        new(UnitMergeParts.Settings, "settings", null, [.. moving.Select(v => v.Key)], [.. moving.Select(v => v.Key)], [.. dropping.Select(v => v.Key)]);
}
