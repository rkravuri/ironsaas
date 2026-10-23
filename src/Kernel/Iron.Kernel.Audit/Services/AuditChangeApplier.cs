using System.Text.Json;

namespace Iron.Kernel.Audit.Services;

/// <summary>What a held retention choice needs to apply: the class and the days.</summary>
public sealed record RetentionChangePayload(string Class, int Days);

/// <summary>What a held placing (no hold yet) or release (the hold) needs to apply, with the requester's reason.</summary>
public sealed record LegalHoldChangePayload(Guid? HoldId, string Scope, string? RecordType, Guid? RecordId, string Reason);

/// <summary>
/// Applies approved changes of retention and legal holds (TF-068, ADR-0052) through the services a direct change uses,
/// as the approver, with the requester's reason.
/// </summary>
public sealed class AuditChangeApplier(RetentionPolicy retention, LegalHolds holds) : IConfigurationChangeApplier
{
    public bool Handles(string kind) => kind is RetentionPolicy.ChooseKind or LegalHolds.PlaceKind or LegalHolds.ReleaseKind;

    public Task<JsonElement> CurrentAsync(HeldChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.Kind == RetentionPolicy.ChooseKind
            ? retention.StateAsync(Read<RetentionChangePayload>(change).Class, cancellationToken)
            : holds.StateAsync(Read<LegalHoldChangePayload>(change), cancellationToken);
    }

    public async Task<Result> ApplyAsync(HeldChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Kind == RetentionPolicy.ChooseKind)
        {
            var choice = Read<RetentionChangePayload>(change);
            var chosen = await retention.ChooseAsync(choice.Class, choice.Days, change.Reason, cancellationToken).ConfigureAwait(false);
            return chosen.IsFailure ? chosen.Error : Result.Success();
        }

        var hold = Read<LegalHoldChangePayload>(change);
        var done = change.Kind == LegalHolds.PlaceKind
            ? await holds.PlaceAsync(hold.Scope, hold.RecordType, hold.RecordId, hold.Reason, cancellationToken).ConfigureAwait(false)
            : await holds.ReleaseAsync(hold.HoldId!.Value, hold.Reason, cancellationToken).ConfigureAwait(false);
        return done.IsFailure ? done.Error : Result.Success();
    }

    private static T Read<T>(HeldChange change) =>
        change.Payload.Deserialize<T>(ChangeJson.Options) ?? throw new InvalidOperationException($"The change {change.Id} carries no {typeof(T).Name}.");
}
