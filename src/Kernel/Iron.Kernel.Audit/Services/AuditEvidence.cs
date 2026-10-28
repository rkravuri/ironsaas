using Iron.Kernel.Api.ControlPlane;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Audit.Services;

/// <summary>The field-level history of a record type, read from the trail's events of record changes (ADR-0051).</summary>
public sealed class RecordHistoryReader(AuditDbContext context) : IRecordHistory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<IReadOnlyList<RecordHistoryEvent>> BetweenAsync(string recordType, DateTimeOffset from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        string[] types = [AuditEvents.RecordCreated, AuditEvents.RecordChanged, AuditEvents.RecordDeleted];
        var query = context.Events.AsNoTracking().Where(e => e.RecordType == recordType && types.Contains(e.EventType) && e.CreatedAt >= from);
        if (to is { } end)
        {
            query = query.Where(e => e.CreatedAt < end);
        }

        var rows = await query.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Where(e => e.RecordId is not null).Select(e => new RecordHistoryEvent(
            e.RecordId!.Value,
            e.CreatedAt,
            e.EventType switch
            {
                AuditEvents.RecordCreated => RecordChangeKinds.Created,
                AuditEvents.RecordDeleted => RecordChangeKinds.Deleted,
                _ => RecordChangeKinds.Changed,
            },
            string.IsNullOrWhiteSpace(e.Changes) ? [] : JsonSerializer.Deserialize<List<FieldChange>>(e.Changes, Json) ?? [],
            e.ActorId,
            e.ImpersonatorId,
            e.Reason))];
    }

    public async Task<DateTimeOffset?> OnlineSinceAsync(CancellationToken cancellationToken) =>
        await context.Events.AsNoTracking().MinAsync(e => (DateTimeOffset?)e.CreatedAt, cancellationToken).ConfigureAwait(false);

    public async Task<DateTimeOffset?> KeptSinceAsync(CancellationToken cancellationToken)
    {
        var events = context.Events.AsNoTracking();
        var first = await events.Where(e => e.HashVersion >= AuditCanonicalForm.FieldHistoryVersion).MinAsync(e => (DateTimeOffset?)e.CreatedAt, cancellationToken).ConfigureAwait(false);
        var lastBefore = await events.Where(e => e.HashVersion < AuditCanonicalForm.FieldHistoryVersion).MaxAsync(e => (DateTimeOffset?)e.CreatedAt, cancellationToken).ConfigureAwait(false);
        if (lastBefore is not null)
        {
            // Events sealed before field history exist: it began with the first one sealed after, or not yet.
            return first ?? lastBefore;
        }

        // Only archived events may be older than the first one with field history: the archive keeps batches by time.
        var archivedBefore = first is { } start
            ? await context.ArchiveBatches.AsNoTracking().AnyAsync(b => b.FirstAt < start, cancellationToken).ConfigureAwait(false)
            : false;
        return archivedBefore ? first : null;
    }
}

/// <summary>
/// The audit trail's part of an evidence pack (TF-067 AC1, ADR-0052): the administrative actions of the period; the
/// retention and the legal holds as they stood at its end; the chain's verifications in the period, which the control
/// plane keeps, and the restores operators accepted; the drills; and the chain's head with its anchor for the manifest.
/// </summary>
public sealed class AuditEvidence(
    AuditDbContext context,
    AuditEventNames names,
    RetentionPolicy retention,
    ControlPlaneClient controlPlane,
    ILogger<AuditEvidence> logger,
    IPrincipals? principals = null) : IEvidenceSource, IEvidenceChain
{
    public const string NoBackupsYet = "Backups start with the first Azure deployment, so no backup or restore drill was recorded for this tenant in the period. The restores of the audit chain that operators accepted in the period are listed below.";

    public async Task<IReadOnlyList<EvidenceTable>> TablesAsync(EvidencePeriod period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        var tables = new List<EvidenceTable>
        {
            await AdministrativeActionsAsync(period, cancellationToken).ConfigureAwait(false),
            await RetentionAsync(period, cancellationToken).ConfigureAwait(false),
            await LegalHoldsAsync(period, cancellationToken).ConfigureAwait(false),
            await VerificationsAsync(period, cancellationToken).ConfigureAwait(false),
        };
        var restores = await RestoresAsync(period, cancellationToken).ConfigureAwait(false);
        tables.Add(restores with { Document = EvidenceDocuments.ChainVerification });
        tables.Add(new EvidenceTable(EvidenceDocuments.BackupDrills, "drills", "Backup and restore drills", [new("drill", "Drill"), new("at", "When"), new("result", "Result")], [], NoBackupsYet));
        tables.Add(restores with { Document = EvidenceDocuments.BackupDrills });
        return tables;
    }

    public async Task<EvidenceChainHead?> HeadAsync(CancellationToken cancellationToken)
    {
        var segment = await context.ChainRestores.AsNoTracking().MaxAsync(r => (int?)r.Segment, cancellationToken).ConfigureAwait(false) ?? AuditChainSegments.First;
        if (controlPlane.IsConfigured)
        {
            try
            {
                using var answer = await controlPlane.SendAsync(HttpMethod.Get, "/api/v1/audit-anchors/latest", null, cancellationToken).ConfigureAwait(false);
                answer.EnsureSuccessStatusCode();
                var latest = await answer.Content.ReadFromJsonAsync<LatestAnchors>(ControlPlaneClient.Json, cancellationToken).ConfigureAwait(false);
                if (latest?.Seal is { } seal)
                {
                    // The anchored head as the tenant's own chain holds it: the event sealed at that position.
                    var sealedEvent = await context.Events.AsNoTracking().Where(e => e.ChainPosition == seal.Position).Select(e => new { e.Hash, e.SealedAt }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                    return new EvidenceChainHead(seal.Segment, seal.Position, sealedEvent?.Hash ?? seal.Hash, sealedEvent?.SealedAt ?? seal.At, new EvidenceAnchor(seal.Id, seal.Segment, seal.Position, seal.Hash, seal.At, seal.ReceivedAt));
                }
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning("The control plane could not be asked for the audit chain's anchored head; the pack names the latest seal without an anchor: {Message}", exception.Message);
            }
        }

        var head = await AuditSealer.HeadAsync(context, cancellationToken).ConfigureAwait(false);
        return head is null ? null : new EvidenceChainHead(segment, head.Position, head.Hash, head.SealedAt, null);
    }

    private async Task<EvidenceTable> AdministrativeActionsAsync(EvidencePeriod period, CancellationToken cancellationToken)
    {
        var types = names.Administrative.ToArray();
        var events = await context.Events.AsNoTracking()
            .Where(e => types.Contains(e.EventType) && e.CreatedAt >= period.From && e.CreatedAt < period.To)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.ChainPosition)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var people = await NamesAsync([.. events.Select(e => e.ActorId), .. events.Where(e => e.ImpersonatorId is not null).Select(e => e.ImpersonatorId!.Value), .. events.Where(e => e.OnBehalfOfId is not null).Select(e => e.OnBehalfOfId!.Value)], cancellationToken).ConfigureAwait(false);
        var oldest = await context.Events.AsNoTracking().MinAsync(e => (DateTimeOffset?)e.CreatedAt, cancellationToken).ConfigureAwait(false);
        var note = oldest is { } since && period.From < since && await context.ArchiveBatches.AnyAsync(cancellationToken).ConfigureAwait(false)
            ? $"Events before {Time(since)} are in the trail's archive: the audit trail's search by date range reads them."
            : null;
        return new EvidenceTable(
            EvidenceDocuments.AdministrativeActions,
            "administrative_actions",
            "Administrative actions",
            [
                new("at", "When (UTC)"), new("event_type", "Event type"), new("event", "Event"), new("outcome", "Outcome"), new("actor_id", "Actor id"), new("actor", "Actor"),
                new("impersonator_id", "Signed in as them by, id"), new("impersonator", "Signed in as them by"), new("on_behalf_of_id", "Acting for, id"), new("target", "Target"),
                new("record_type", "Record type"), new("record_id", "Record id"), new("reason", "Reason"), new("severity", "Severity"), new("chain_position", "Chain position"),
            ],
            [.. events.Select(e => (IReadOnlyList<string?>)
            [
                Time(e.CreatedAt), e.EventType, names.NameOf(e.EventType), e.Outcome, e.ActorId.ToString("D"), Name(people, e.ActorId),
                e.ImpersonatorId?.ToString("D"), e.ImpersonatorId is { } impersonator ? Name(people, impersonator, ImpersonatorOf(e.Details)) : null, e.OnBehalfOfId?.ToString("D"), e.Target,
                e.RecordType, e.RecordId?.ToString("D"), e.Reason, e.Severity, e.ChainPosition?.ToString(CultureInfo.InvariantCulture),
            ])],
            note);
    }

    /// <summary>Each class's retention at the period's end, from the retention changes in the trail: the last before the end, or the one before the first after it.</summary>
    private async Task<EvidenceTable> RetentionAsync(EvidencePeriod period, CancellationToken cancellationToken)
    {
        var changes = await context.Events.AsNoTracking().Where(e => e.EventType == AuditEvents.RetentionChanged).OrderBy(e => e.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var classes = await retention.ClassesAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var view in classes)
        {
            var mine = changes.Where(e => e.Target == view.Class.Code).Select(e => (e.CreatedAt, e.ActorId, Details: Details(e.Details))).ToList();
            var last = mine.LastOrDefault(c => c.CreatedAt < period.To);
            var next = mine.FirstOrDefault(c => c.CreatedAt >= period.To);
            var days = last.Details is not null ? last.Details.GetValueOrDefault("after_days")
                : next.Details is not null ? next.Details.GetValueOrDefault("before_days")
                : view.Days.ToString(CultureInfo.InvariantCulture);
            rows.Add([view.Class.Code, view.Class.Description, days, view.MinimumDays.ToString(CultureInfo.InvariantCulture), view.MaximumDays.ToString(CultureInfo.InvariantCulture), last.Details is null ? null : Time(last.CreatedAt)]);
        }

        return new EvidenceTable(
            EvidenceDocuments.SecuritySettings,
            "retention",
            "Retention at the period's end",
            [new("class", "Class"), new("description", "What it keeps"), new("days", "Days kept"), new("minimum_days", "Operator's minimum"), new("maximum_days", "Operator's maximum"), new("chosen_at", "Chosen (UTC)")],
            rows);
    }

    private async Task<EvidenceTable> LegalHoldsAsync(EvidencePeriod period, CancellationToken cancellationToken)
    {
        var holds = await context.LegalHolds.AsNoTracking()
            .Where(h => h.CreatedAt < period.To && (h.ReleasedAt == null || h.ReleasedAt >= period.To))
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var people = await NamesAsync([.. holds.Select(h => h.CreatedBy)], cancellationToken).ConfigureAwait(false);
        return new EvidenceTable(
            EvidenceDocuments.SecuritySettings,
            "legal_holds",
            "Legal holds standing at the period's end",
            [new("id", "Hold id"), new("scope", "On"), new("record_type", "Record type"), new("record_id", "Record id"), new("reason", "Reason"), new("placed_at", "Placed (UTC)"), new("placed_by", "Placed by")],
            [.. holds.Select(h => (IReadOnlyList<string?>)[h.Id.ToString("D"), h.Scope, h.RecordType, h.RecordId?.ToString("D"), h.Reason, Time(h.CreatedAt), Name(people, h.CreatedBy)])]);
    }

    private async Task<EvidenceTable> VerificationsAsync(EvidencePeriod period, CancellationToken cancellationToken)
    {
        IReadOnlyList<Verification> found = [];
        string? note = null;
        if (!controlPlane.IsConfigured)
        {
            note = "This unit has no control plane to ask: the verifications are recorded there (ADR-0050).";
        }
        else
        {
            try
            {
                var path = string.Create(CultureInfo.InvariantCulture, $"/api/v1/audit-verifications?from={Uri.EscapeDataString(period.From.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(period.To.ToString("O", CultureInfo.InvariantCulture))}");
                using var answer = await controlPlane.SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
                answer.EnsureSuccessStatusCode();
                found = (await answer.Content.ReadFromJsonAsync<VerificationList>(ControlPlaneClient.Json, cancellationToken).ConfigureAwait(false))?.Verifications ?? [];
            }
            catch (HttpRequestException exception)
            {
                note = $"The control plane could not be asked for the verifications when the pack was made: {exception.Message}";
            }
        }

        if (note is null && found.Count == 0)
        {
            note = "No verification of the audit chain was recorded in the period.";
        }

        return new EvidenceTable(
            EvidenceDocuments.ChainVerification,
            "verifications",
            "Verifications of the audit chain in the period",
            [
                new("id", "Record id"), new("verified_at", "Verified (UTC)"), new("full", "Full"), new("from_position", "From position"), new("to_position", "To position"),
                new("result", "Result"), new("events_checked", "Events checked"), new("problems", "Problems"), new("restore_position", "Restore point"), new("restore_at", "Restore point's time (UTC)"),
            ],
            [.. found.Select(v => (IReadOnlyList<string?>)
            [
                v.Id?.ToString("D"), Time(v.VerifiedAt), v.Full ? "yes" : "no", v.FromPosition.ToString(CultureInfo.InvariantCulture), v.ToPosition.ToString(CultureInfo.InvariantCulture),
                v.Result, v.EventsChecked.ToString(CultureInfo.InvariantCulture), string.Join("; ", (v.Problems ?? []).Select(p => $"{p.Kind} at {p.Position}: {p.Message}")),
                v.RestorePosition?.ToString(CultureInfo.InvariantCulture), v.RestoreAt is { } at ? Time(at) : null,
            ])],
            note);
    }

    private async Task<EvidenceTable> RestoresAsync(EvidencePeriod period, CancellationToken cancellationToken)
    {
        var restores = await context.ChainRestores.AsNoTracking().Where(r => r.AcceptedAt >= period.From && r.AcceptedAt < period.To).OrderBy(r => r.Segment).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new EvidenceTable(
            EvidenceDocuments.ChainVerification,
            "accepted_restores",
            "Restores of the audit chain accepted in the period",
            [new("segment", "Segment"), new("restore_position", "Restore point"), new("restore_at", "Restore point's time (UTC)"), new("lost", "Positions lost"), new("ticket", "Ticket"), new("reason", "Reason"), new("accepted_by", "Accepted by"), new("accepted_at", "Accepted (UTC)")],
            [.. restores.Select(r => (IReadOnlyList<string?>)[r.Segment.ToString(CultureInfo.InvariantCulture), r.RestorePosition.ToString(CultureInfo.InvariantCulture), Time(r.RestoreAt), $"{r.LostFrom} to {r.LostTo}", r.Ticket, r.Reason, r.AcceptedBy, Time(r.AcceptedAt)])],
            restores.Count == 0 ? "No restore was accepted in the period." : null);
    }

    private async Task<IReadOnlyDictionary<Guid, PrincipalSummary>> NamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        principals is null || ids.Count == 0 ? new Dictionary<Guid, PrincipalSummary>() : await principals.FindAsync([.. ids.Distinct()], cancellationToken).ConfigureAwait(false);

    private static string Name(IReadOnlyDictionary<Guid, PrincipalSummary> people, Guid id, string? fallback = null) =>
        id == SystemPrincipal.SystemId ? "System" : people.TryGetValue(id, out var person) ? person.DisplayName : fallback ?? id.ToString("D");

    private static string? ImpersonatorOf(string? details) => Details(details)?.GetValueOrDefault(AuditWriter.ImpersonatorKey);

    private static Dictionary<string, string>? Details(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);

    private static string Time(DateTimeOffset at) => CanonicalJson.Time(at);

    private sealed record LatestAnchor(long Position, string Hash, DateTimeOffset At, DateTimeOffset ReceivedAt, int Segment, Guid? Id);

    private sealed record LatestAnchors(LatestAnchor? Seal);

    private sealed record Problem(string Kind, long Position, string Message);

    private sealed record Verification(Guid? Id, bool Full, long FromPosition, long ToPosition, string Result, int EventsChecked, IReadOnlyList<Problem>? Problems, long? RestorePosition, DateTimeOffset? RestoreAt, DateTimeOffset VerifiedAt);

    private sealed record VerificationList(IReadOnlyList<Verification> Verifications);
}
