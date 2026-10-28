using System.Globalization;
using System.Net.Http.Json;
using Iron.Kernel.Api.ControlPlane;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Encryption;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Keys.Contracts;
using Iron.Kernel.Keys.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Keys.Services;

/// <summary>Bound from the <c>Keys</c> section: how often the unit's worker asks the control plane about its keys.</summary>
public sealed class KeyWorkerOptions
{
    public const string SectionName = "Keys";

    public int PollSeconds { get; set; } = 15;
}

/// <summary>
/// The unit's side of key rotation (TF-075, ADR-0054), one pass: ask the control plane for the tenant's keys and
/// rotations as the unit's own client; record each new rotation in the tenant's audit trail with who asked and why, and
/// each retirement; tell the control plane the tenant's policy for its data and index keys; and move every value of a
/// rotated data or index key to the new version, reporting the progress, then that it finished, so the fleet retires
/// the old version. A pass that cannot reach the control plane tries again at the next.
/// </summary>
public sealed class KeyRotationSync(
    KeysDbContext context,
    ControlPlaneClient client,
    FieldReencryption reencryption,
    ISettings settings,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    TimeProvider time,
    ILogger<KeyRotationSync> logger)
{
    public const string Rotated = "rotated";
    public const string Finished = "finished";
    public const string Retired = "retired";
    public const string DatabaseKey = "database";

    // The progress is reported to the control plane at most this often while values move.
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(1);

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!client.IsConfigured)
        {
            return 0;
        }

        using var response = await client.SendAsync(HttpMethod.Get, "/api/v1/key-rotations", null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("The control plane answered {Status} to the unit's question about its keys; it asks again at the next pass.", (int)response.StatusCode);
            return 0;
        }

        var answer = await response.Content.ReadFromJsonAsync<KeysAnswer>(ControlPlaneClient.Json, cancellationToken).ConfigureAwait(false) ?? new KeysAnswer([], []);
        await RecordStatesAsync(answer.Keys, cancellationToken).ConfigureAwait(false);
        var recorded = await RecordRotationsAsync(answer.Rotations, cancellationToken).ConfigureAwait(false);
        await ReportPolicyAsync(answer.Keys, cancellationToken).ConfigureAwait(false);
        return recorded + await ReencryptAsync(answer.Rotations, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordStatesAsync(IReadOnlyList<KeyAnswer> keys, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var states = await context.States.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            var state = states.SingleOrDefault(s => s.Key == key.Key);
            if (state is null)
            {
                state = new KeyState(key.Key);
                context.States.Add(state);
            }

            state.Answered(key.Version, key.MinimumVersion, key.RotatedAt, key.NextRotationAt, key.RotationDays, now);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RecordRotationsAsync(IReadOnlyList<RotationAnswer> rotations, CancellationToken cancellationToken)
    {
        var recorded = 0;
        foreach (var rotation in rotations.Where(r => r.ToVersion is not null && r.RotatedAt is not null))
        {
            var entry = await context.Rotations.SingleOrDefaultAsync(r => r.Id == rotation.Id, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                entry = new KeyRotationEntry(rotation.Id, rotation.Key, rotation.Trigger, rotation.RequestedBy, rotation.Reason, rotation.FromVersion ?? rotation.ToVersion!.Value - 1, rotation.ToVersion!.Value, rotation.RotatedAt!.Value);
                context.Rotations.Add(entry);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await audit.WriteAsync(Event(KeysAuditEvents.KeyRotated, entry, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["from_version"] = entry.FromVersion.ToString(CultureInfo.InvariantCulture),
                    ["to_version"] = entry.ToVersion.ToString(CultureInfo.InvariantCulture),
                }), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Recorded the rotation of the tenant's {Key} key to version {Version}, asked by {RequestedBy}.", entry.Key, entry.ToVersion, entry.RequestedBy);
                recorded++;
            }

            if (rotation.RetiredAt is { } retiredAt && entry.RetiredAt is null)
            {
                await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                entry.Retire(retiredAt);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                // A database password has no versions that keep working: its rotation says all there is.
                if (entry.Key != DatabaseKey)
                {
                    await audit.WriteAsync(Event(KeysAuditEvents.KeyRetired, entry, new Dictionary<string, string>(StringComparer.Ordinal) { ["minimum_version"] = entry.ToVersion.ToString(CultureInfo.InvariantCulture) }), cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                recorded++;
            }
        }

        return recorded;
    }

    private async Task ReportPolicyAsync(IReadOnlyList<KeyAnswer> keys, CancellationToken cancellationToken)
    {
        var days = await settings.GetAsync(KeysSettings.DataRotationDays, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        if (keys.FirstOrDefault(k => k.Key == KeyPurpose.Data.Word()) is not { } data || data.RotationDays == days)
        {
            return;
        }

        using var response = await client.SendAsync(HttpMethod.Put, "/api/v1/key-rotations/policy", new { DataDays = days }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("The control plane answered {Status} to the tenant's key policy; it is sent again at the next pass.", (int)response.StatusCode);
        }
    }

    private async Task<int> ReencryptAsync(IReadOnlyList<RotationAnswer> rotations, CancellationToken cancellationToken)
    {
        var moved = 0;
        foreach (var rotation in rotations.Where(r => r.State is Rotated or Finished && KeyPurposes.TryParse(r.Key, out var purpose) && purpose != KeyPurpose.Signing))
        {
            var entry = await context.Rotations.SingleOrDefaultAsync(r => r.Id == rotation.Id, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                continue;
            }

            if (entry.FinishedAt is not null)
            {
                // Moved here, but the control plane did not hear it: it is told again.
                if (rotation.State == Rotated && entry.FinishReportedAt is null && await ReportAsync(entry, finished: true, cancellationToken).ConfigureAwait(false))
                {
                    entry.FinishReported(time.GetUtcNow());
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (rotation.State == Finished)
            {
                // Finished before this process asked, as after a restart: only the record here was behind.
                entry.Finish(rotation.FinishedAt ?? time.GetUtcNow());
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await ReencryptAsync(entry, cancellationToken).ConfigureAwait(false);
            moved++;
        }

        return moved;
    }

    private async Task ReencryptAsync(KeyRotationEntry entry, CancellationToken cancellationToken)
    {
        KeyPurposes.TryParse(entry.Key, out var purpose);
        entry.Started(time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var reportedAt = DateTimeOffset.MinValue;

        var progress = await reencryption.RunAsync(purpose, entry.ToVersion, async (step, token) =>
        {
            entry.Progress(step.Done, step.Total);
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            if (time.GetUtcNow() - reportedAt >= ReportEvery)
            {
                await ReportAsync(entry, finished: false, token).ConfigureAwait(false);
                reportedAt = time.GetUtcNow();
            }
        }, cancellationToken).ConfigureAwait(false);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        entry.Progress(progress.Done, progress.Total);
        entry.Finish(time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(Event(KeysAuditEvents.ReencryptionFinished, entry, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["to_version"] = entry.ToVersion.ToString(CultureInfo.InvariantCulture),
            ["values_moved"] = progress.Done.ToString(CultureInfo.InvariantCulture),
        }), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (await ReportAsync(entry, finished: true, cancellationToken).ConfigureAwait(false))
        {
            entry.FinishReported(time.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("Moved {Count} values of the tenant to version {Version} of its {Key} key.", progress.Done, entry.ToVersion, entry.Key);
    }

    private async Task<bool> ReportAsync(KeyRotationEntry entry, bool finished, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(HttpMethod.Post, $"/api/v1/key-rotations/{entry.Id}/progress", new { entry.Done, entry.Total, Finished = finished }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && finished)
        {
            logger.LogWarning("The control plane answered {Status} to the re-encryption's end; it is told again at the next pass.", (int)response.StatusCode);
        }

        return response.IsSuccessStatusCode;
    }

    private static AuditEvent Event(string type, KeyRotationEntry entry, Dictionary<string, string> details)
    {
        details["key"] = entry.Key;
        details["rotation_id"] = entry.Id.ToString("D");
        details["requested_by"] = entry.RequestedBy;
        details["trigger"] = entry.Trigger;
        return new AuditEvent(type, AuditOutcome.Success, SystemPrincipal.SystemId, entry.Key, details, KeyRotationEntryRecordType, entry.Id, entry.Reason);
    }

    private const string KeyRotationEntryRecordType = "key_rotation";

    private sealed record KeysAnswer(IReadOnlyList<KeyAnswer> Keys, IReadOnlyList<RotationAnswer> Rotations);

    private sealed record KeyAnswer(string Key, int Version, int MinimumVersion, DateTimeOffset? RotatedAt, DateTimeOffset NextRotationAt, int RotationDays);

    private sealed record RotationAnswer(Guid Id, string Key, string Trigger, string RequestedBy, string Reason, string State, int? FromVersion, int? ToVersion, DateTimeOffset? RotatedAt, DateTimeOffset? FinishedAt, DateTimeOffset? RetiredAt);
}

/// <summary>The unit's worker loop for keys: one pass every few seconds, which the worker host alone runs.</summary>
public sealed class KeyWorker(IServiceScopeFactory scopes, KeyWorkerOptions options, TimeProvider time, ILogger<KeyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<KeyRotationSync>().RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The key worker's pass failed; it tries again at the next.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>A key check found by its exact value, with the value it opened to.</summary>
public sealed record KeyCheckMatch(Guid Id, string Label, string Value, DateTimeOffset CreatedAt);

/// <summary>
/// Key checks (ADR-0054): a value stored encrypted with the tenant's data key, with its keyed hash, and found again by
/// its exact value; after a rotation, finding it proves the tenant's keys still read and find. The value is never logged
/// and its history keeps only that it was set.
/// </summary>
public sealed class KeyChecks(KeysDbContext context, IFieldEncryption encryption, IUnitOfWork unitOfWork, IAuditWriter audit, ICurrentPrincipal actor)
{
    public async Task<Result<KeyCheck>> StoreAsync(string? label, string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return Error.Validation(KeysErrors.CheckLabelRequired, "A key check needs a label.", new FieldError("label", "required", "Give the check a label you will recognise."));
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return Error.Validation(KeysErrors.CheckValueRequired, "A key check needs a value.", new FieldError("value", "required", "Type the value to find again."));
        }

        var check = new KeyCheck(label.Trim(), await encryption.SealSearchableAsync(value, cancellationToken).ConfigureAwait(false));
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.Checks.Add(check);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(KeysAuditEvents.CheckStored, AuditOutcome.Success, actor.Id, check.Label, null, KeyCheck.RecordType, check.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return check;
    }

    /// <summary>The checks whose value is exactly this one, through the keyed hash; a part of a value finds nothing.</summary>
    public async Task<IReadOnlyList<KeyCheckMatch>> FindAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hashes = (await encryption.SearchHashesAsync(value, cancellationToken).ConfigureAwait(false)).ToList();
        var found = await context.Checks.AsNoTracking().Where(c => hashes.Contains(c.ValueHash!)).OrderBy(c => c.CreatedAt).Take(20).ToListAsync(cancellationToken).ConfigureAwait(false);
        var values = (await encryption.DecryptAsync([.. found.Select(c => c.Value!)], cancellationToken).ConfigureAwait(false)).Select(System.Text.Encoding.UTF8.GetString).ToList();
        return [.. found.Select((check, i) => new KeyCheckMatch(check.Id, check.Label, values[i], check.CreatedAt))];
    }
}
