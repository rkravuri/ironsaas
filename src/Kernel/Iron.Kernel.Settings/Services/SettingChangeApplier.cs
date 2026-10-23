using System.Text.Json;

namespace Iron.Kernel.Settings.Services;

/// <summary>What a held setting change needs to apply: the key, the place (level and target), and the value, none for a reset.</summary>
public sealed record SettingChangePayload(string Key, string Level, Guid? TargetId, JsonElement? Value)
{
    public SettingPlace Place => Enum.Parse<SettingLevel>(Level, ignoreCase: true) switch
    {
        SettingLevel.Unit => SettingPlace.Unit(TargetId ?? Guid.Empty),
        SettingLevel.User => SettingPlace.User(TargetId ?? Guid.Empty),
        _ => SettingPlace.Tenant,
    };
}

/// <summary>
/// Applies an approved change of a governed setting (ADR-0052): the save or reset the requester asked for, through the
/// writer's own checks, as the approver. The state compared before it applies is the value set at the place.
/// </summary>
public sealed class SettingChangeApplier(SettingsWriter writer, SettingsRefresher refresher) : IConfigurationChangeApplier
{
    public bool Handles(string kind) => kind is SettingsWriter.SetKind or SettingsWriter.ResetKind;

    public Task<JsonElement> CurrentAsync(HeldChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var payload = Payload(change);
        return writer.StateAsync(payload.Key, payload.Place, cancellationToken);
    }

    public Task<Result> ApplyAsync(HeldChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var payload = Payload(change);
        return change.Kind == SettingsWriter.ResetKind || payload.Value is null
            ? writer.ResetAsync(payload.Key, payload.Place, cancellationToken)
            : writer.SetAsync(payload.Key, payload.Place, payload.Value.Value, cancellationToken);
    }

    /// <summary>
    /// The writer reads the settings back into the unit's cache after its own commit, which inside an approval is not yet
    /// the database's: read them again once the approval committed, so the approved value applies at once, not within 30 s.
    /// </summary>
    public async Task AppliedAsync(HeldChange change, CancellationToken cancellationToken) =>
        await refresher.RefreshOnceAsync(cancellationToken).ConfigureAwait(false);

    private static SettingChangePayload Payload(HeldChange change) =>
        change.Payload.Deserialize<SettingChangePayload>(ChangeJson.Options) ?? throw new InvalidOperationException($"The change {change.Id} carries no setting.");
}
