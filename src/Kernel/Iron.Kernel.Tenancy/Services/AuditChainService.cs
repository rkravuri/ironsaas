using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>
/// The tenants' audit chains in the control plane (TF-061, ADR-0050). A unit's own client sends its anchors, which are
/// that unit's tenant's whatever the request says, and are written once; the same position anchored again with another
/// hash is refused and raises the alert. The fleet asks what is due and records each verification here; one that did not
/// pass raises the alert: an error line starting <c>ALERT:</c>, the counter <c>audit.verification_failures</c>, and a
/// high-severity event in the control plane's audit trail, beside the record the operators read. After a restore, an
/// operator accepts its restore point with a ticket, a reason and a fresh token; the chain continues as a new segment
/// from there, and every anchor is kept in the segment that covers its position (ADR-0050).
/// </summary>
public sealed partial class AuditChainService(
    TenancyDbContext context,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    OperatorNames names,
    ILogger<AuditChainService> logger) : ITenantAuditChains
{
    /// <summary>A tenant is verified once a day (TF-061), and in full once a month.</summary>
    public static readonly TimeSpan Daily = TimeSpan.FromDays(1);

    public static readonly TimeSpan Monthly = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The caller's tenant, when the caller is a unit's client.</summary>
    public async Task<string?> TenantOfCallerAsync(CancellationToken cancellationToken) =>
        await context.UnitClients.AsNoTracking()
            .Where(c => c.PrincipalId == actor.Id)
            .Select(c => c.TenantSlug)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<Result<(AuditAnchorInfo Anchor, bool Created)>> AppendAsync(string kind, long position, string hash, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var slug = await TenantOfCallerAsync(cancellationToken).ConfigureAwait(false);
        if (slug is null)
        {
            return Error.Forbidden(TenancyErrors.AuditAnchorNotAUnit, "Only a tenant's unit sends audit anchors, and only its own.");
        }

        if (kind is not (AuditAnchorKinds.Seal or AuditAnchorKinds.Pruned))
        {
            return Error.Validation(TenancyErrors.AuditAnchorKindUnknown, $"An anchor is a '{AuditAnchorKinds.Seal}' or a '{AuditAnchorKinds.Pruned}'.", new FieldError("kind", "unknown", "Choose seal or pruned."));
        }

        if (position < 1 || !HashPattern().IsMatch(hash ?? string.Empty))
        {
            return Error.Validation("tenancy.audit_anchor.invalid", "An anchor has a position from 1 and a SHA-256 in lowercase hexadecimal.");
        }

        // A position belongs to the segment that covers it: after an accepted restore, a reused position is the new
        // segment's, and its old anchor stays in the segment before as the record of what the restore lost.
        var segment = AuditChainSegments.Covering(position, Segments(await RestoresAsync(slug, cancellationToken).ConfigureAwait(false)));
        var existing = await context.AuditAnchors.AsNoTracking()
            .SingleOrDefaultAsync(a => a.TenantSlug == slug && a.Segment == segment && a.Kind == kind && a.Position == position, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (string.Equals(existing.Hash, hash, StringComparison.Ordinal))
            {
                return (Info(existing), false);
            }

            // The unit claims another hash for a position it anchored before: its chain was rewritten after the anchor.
            AuditChainMetrics.AnchorConflicts.Add(1);
            logger.LogError("ALERT: tenant {Slug} sent a {Kind} anchor for position {Position} with hash {Hash}, which it anchored before with {Anchored}.", slug, kind, position, hash, existing.Hash);
            var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = kind, ["position"] = position.ToString(System.Globalization.CultureInfo.InvariantCulture), ["segment"] = segment.ToString(System.Globalization.CultureInfo.InvariantCulture), ["anchored_hash"] = existing.Hash, ["sent_hash"] = hash! };
            await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.AuditAnchorConflict, AuditOutcome.Denied, actor.Id, slug, details, "tenant", null, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
            return Error.Conflict(TenancyErrors.AuditAnchorConflict, $"Position {position} was anchored before with another hash; the anchor stands as it was.");
        }

        var anchor = new AuditAnchorRecord(slug, kind, position, hash!, at, segment);
        context.AuditAnchors.Add(anchor);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (Info(anchor), true);
    }

    /// <summary>
    /// Where the caller's tenant's unit resumes sending: the latest seal anchor of the current segment, or the restore point
    /// a new segment starts from when it has none yet; the latest pruned anchor; the current segment; and every accepted
    /// restore, which the unit records in its own trail (ADR-0050).
    /// </summary>
    public async Task<Result<UnitChainState>> LatestOfCallerAsync(CancellationToken cancellationToken)
    {
        var slug = await TenantOfCallerAsync(cancellationToken).ConfigureAwait(false);
        if (slug is null)
        {
            return Error.Forbidden(TenancyErrors.AuditAnchorNotAUnit, "Only a tenant's unit reads its own anchors.");
        }

        var restores = await RestoresAsync(slug, cancellationToken).ConfigureAwait(false);
        var current = AuditChainSegments.Current(Segments(restores));
        var seal = await context.AuditAnchors.AsNoTracking()
            .Where(a => a.TenantSlug == slug && a.Kind == AuditAnchorKinds.Seal && a.Segment == current)
            .OrderByDescending(a => a.Position)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var sealInfo = seal is not null
            ? Info(seal)
            : restores.Count > 0
                ? new AuditAnchorInfo(AuditAnchorKinds.Seal, restores[^1].RestorePosition, restores[^1].PreviousHash, restores[^1].RestoreAt, restores[^1].AcceptedAt, current)
                : null;
        return new UnitChainState(sealInfo, await LatestAnchorAsync(slug, AuditAnchorKinds.Pruned, cancellationToken).ConfigureAwait(false), current, restores);
    }

    /// <summary>
    /// An operator accepts the restore point the tenant's latest verification names (ADR-0050), with a ticket and a reason:
    /// the chain continues as a new segment from the position after it, linking to its hash, and the range the restore
    /// lost is kept with the record. A full verification is asked for at once, and the unit records the acceptance in
    /// its own trail at its next look.
    /// </summary>
    public async Task<Result<(AuditRestoreInfo Restore, Guid VerificationRequestId)>> AcceptRestoreAsync(string slug, string? ticket, string? reason, CancellationToken cancellationToken)
    {
        var why = reason?.Trim();
        var ticketText = ticket?.Trim();
        if (string.IsNullOrEmpty(why) || string.IsNullOrEmpty(ticketText) || why.Length > AuditRestoreRecord.ReasonMaxLength || ticketText.Length > AuditRestoreRecord.TicketMaxLength)
        {
            return Error.Validation(
                TenancyErrors.AuditRestoreInvalid,
                "Accepting a restore needs a ticket and a reason.",
                new FieldError("ticket", "required", $"Name the incident's ticket, {AuditRestoreRecord.TicketMaxLength} characters at most."),
                new FieldError("reason", "required", $"Say why the restore is accepted, {AuditRestoreRecord.ReasonMaxLength} characters at most."));
        }

        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        var latest = await LatestVerificationAsync(slug, false, cancellationToken).ConfigureAwait(false);
        if (latest is not { Result: AuditVerificationResults.Restored, RestorePosition: { } point })
        {
            return Error.Conflict(TenancyErrors.AuditRestoreNone, $"The latest verification of '{slug}' names no restore point: there is no restore to accept.");
        }

        var restores = await RestoresAsync(slug, cancellationToken).ConfigureAwait(false);
        if (restores.Count > 0 && restores[^1].AcceptedAt >= latest.At)
        {
            return Error.Conflict(TenancyErrors.AuditRestoreNone, $"The restore point at position {restores[^1].RestorePosition} was accepted already; the verification it asked for runs within a minute.");
        }

        var segments = Segments(restores);
        var anchors = (await AnchorsAsync(slug, cancellationToken).ConfigureAwait(false)).Select(a => new AuditAnchor(a.Kind, a.Position, a.Hash, a.At, a.Segment)).ToList();
        if (AuditChainSegments.Accept(point, anchors, segments) is not { } accepted)
        {
            return Error.Conflict(TenancyErrors.AuditRestoreNoAnchor, $"No anchor counts at position {point}: ask for a full verification (make verify-audit TENANT={slug} FULL=1) and accept the restore point it names.");
        }

        var operatorName = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var record = new AuditRestoreRecord(slug, accepted.Segment.Number, accepted.Segment.StartPosition, accepted.Segment.PreviousHash, accepted.RestorePosition, accepted.RestoreAt, accepted.LostFrom, accepted.LostTo, ticketText, why, actor.Id, operatorName);
        context.AuditRestores.Add(record);
        var request = new AuditVerificationRequest(slug, true, actor.Id);
        context.AuditVerificationRequests.Add(request);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["segment"] = record.Segment.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["restore_position"] = record.RestorePosition.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["lost_from"] = record.LostFrom.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["lost_to"] = record.LostTo.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ticket"] = ticketText,
            ["verification_request_id"] = request.Id.ToString("D"),
        };
        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.AuditRestoreAccepted, AuditOutcome.Success, actor.Id, slug, details, "tenant", tenant.Id, why, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogWarning(
            "The restore of tenant {Slug}'s audit chain was accepted by {Operator}, ticket {Ticket}: segment {Segment} continues from position {Start}; positions {LostFrom} to {LostTo} were lost.",
            slug,
            operatorName,
            ticketText,
            record.Segment,
            record.StartPosition,
            record.LostFrom,
            record.LostTo);
        return (Info(record), request.Id);
    }

    public async Task<IReadOnlyList<AuditRestoreInfo>> RestoresAsync(string slug, CancellationToken cancellationToken) =>
        (await context.AuditRestores.AsNoTracking()
            .Where(r => r.TenantSlug == slug)
            .OrderBy(r => r.Segment)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .Select(Info)
        .ToList();

    /// <summary>The chain's segments after the first, as the rules of <see cref="AuditChainSegments"/> take them.</summary>
    public static IReadOnlyList<ChainSegment> Segments(IReadOnlyList<AuditRestoreInfo> restores)
    {
        ArgumentNullException.ThrowIfNull(restores);
        return [.. restores.Select(r => new ChainSegment(r.Segment, r.StartPosition, r.PreviousHash))];
    }

    /// <summary>The caller's tenant's latest verification, for the unit's chain page.</summary>
    public async Task<Result<AuditVerificationInfo?>> LatestVerificationOfCallerAsync(CancellationToken cancellationToken)
    {
        var slug = await TenantOfCallerAsync(cancellationToken).ConfigureAwait(false);
        if (slug is null)
        {
            return Error.Forbidden(TenancyErrors.AuditAnchorNotAUnit, "Only a tenant's unit reads its own chain's verification.");
        }

        return await LatestVerificationAsync(slug, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The verification records of the caller's tenant made in a period, oldest first (TF-067 AC1, ADR-0052): what an
    /// evidence pack lists as the chain's verification results. A unit reads its own only.
    /// </summary>
    public async Task<Result<IReadOnlyList<AuditVerificationInfo>>> VerificationsOfCallerAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var slug = await TenantOfCallerAsync(cancellationToken).ConfigureAwait(false);
        if (slug is null)
        {
            return Error.Forbidden(TenancyErrors.AuditAnchorNotAUnit, "Only a tenant's unit reads its own chain's verifications.");
        }

        var records = await context.AuditVerifications.AsNoTracking()
            .Where(v => v.TenantSlug == slug && v.CreatedAt >= from && v.CreatedAt < to)
            .OrderBy(v => v.CreatedAt)
            .Take(5000)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records.Select(Info).ToList();
    }

    /// <summary>An operator asks for a verification now (TF-061); the fleet's minute loop runs it.</summary>
    public async Task<Result<AuditVerificationRequest>> RequestAsync(string slug, bool full, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return Error.Conflict(TenancyErrors.NotProvisionable, $"Tenant '{slug}' is {tenant.Status.ToString().ToLowerInvariant()}; only an active tenant's chain is verified.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var request = new AuditVerificationRequest(slug, full, actor.Id);
        context.AuditVerificationRequests.Add(request);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["full"] = full ? "true" : "false", ["request_id"] = request.Id.ToString("D") };
        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.AuditVerificationRequested, AuditOutcome.Success, actor.Id, slug, details, "tenant", tenant.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return request;
    }

    public async Task<(AuditVerificationRequest Request, AuditVerificationInfo? Verification)?> FindRequestAsync(string slug, Guid id, CancellationToken cancellationToken)
    {
        var request = await context.AuditVerificationRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.TenantSlug == slug, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return null;
        }

        var record = request.VerificationId is { } verificationId
            ? await context.AuditVerifications.AsNoTracking().SingleOrDefaultAsync(v => v.Id == verificationId, cancellationToken).ConfigureAwait(false)
            : null;
        return (request, record is null ? null : Info(record));
    }

    public async Task BindUnitClientAsync(string slug, Guid principalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var existing = await context.UnitClients.SingleOrDefaultAsync(c => c.TenantSlug == slug, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            context.UnitClients.Add(new UnitClient(slug, principalId));
        }
        else if (existing.PrincipalId != principalId)
        {
            existing.Rebind(principalId);
        }
        else
        {
            return;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditAnchorInfo>> AnchorsAsync(string slug, CancellationToken cancellationToken) =>
        (await context.AuditAnchors.AsNoTracking()
            .Where(a => a.TenantSlug == slug)
            .OrderBy(a => a.Position)
            .ThenBy(a => a.Kind)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .Select(Info)
        .ToList();

    public async Task<AuditVerificationInfo?> LatestVerificationAsync(string slug, bool fullOnly, CancellationToken cancellationToken)
    {
        var record = await context.AuditVerifications.AsNoTracking()
            .Where(v => v.TenantSlug == slug && (!fullOnly || v.Full))
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : Info(record);
    }

    public async Task<IReadOnlyList<AuditVerificationDue>> DueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = new List<AuditVerificationDue>();
        var tenants = await context.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .Select(t => new { t.Slug, t.DatabaseName, t.StorageContainer })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var places = tenants.ToDictionary(t => t.Slug, StringComparer.Ordinal);

        var requests = await context.AuditVerificationRequests.AsNoTracking()
            .Where(r => r.VerificationId == null)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        due.AddRange(requests.Where(r => places.ContainsKey(r.TenantSlug)).Select(r => new AuditVerificationDue(r.TenantSlug, places[r.TenantSlug].DatabaseName, places[r.TenantSlug].StorageContainer, r.Full, r.Id)));

        var latest = await context.AuditVerifications.AsNoTracking()
            .GroupBy(v => v.TenantSlug)
            .Select(g => new { Slug = g.Key, Last = g.Max(v => v.CreatedAt), LastFull = g.Where(v => v.Full).Max(v => (DateTimeOffset?)v.CreatedAt) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var bySlug = latest.ToDictionary(l => l.Slug, StringComparer.Ordinal);
        foreach (var tenant in tenants.Where(t => due.All(d => d.TenantSlug != t.Slug)))
        {
            var last = bySlug.GetValueOrDefault(tenant.Slug);
            if (last is null || now - last.Last >= Daily)
            {
                var full = last?.LastFull is not { } lastFull || now - lastFull >= Monthly;
                due.Add(new AuditVerificationDue(tenant.Slug, tenant.DatabaseName, tenant.StorageContainer, full, null));
            }
        }

        return due;
    }

    public async Task RecordVerificationAsync(AuditVerificationInfo verification, Guid? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var record = new AuditVerificationRecord(
            verification.TenantSlug,
            verification.JobId,
            verification.Full,
            verification.FromPosition,
            verification.ToPosition,
            verification.ToHash,
            verification.Result,
            verification.EventsChecked,
            JsonSerializer.Serialize(verification.Problems, Json),
            verification.RestorePosition,
            verification.RestoreAt);
        context.AuditVerifications.Add(record);
        if (requestId is { } id && await context.AuditVerificationRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false) is { } request)
        {
            request.Answer(record.Id);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (verification.Result != AuditVerificationResults.Passed)
        {
            var first = verification.Problems.FirstOrDefault();
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["result"] = verification.Result,
                ["problems"] = verification.Problems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["verification_id"] = record.Id.ToString("D"),
            };
            if (first is not null)
            {
                details["first_problem"] = first.Message;
                if (first.EventId is { } eventId)
                {
                    details["event_id"] = eventId.ToString("D");
                }
            }

            if (verification.RestorePosition is { } restore)
            {
                details["restore_position"] = restore.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var eventType = verification.Result == AuditVerificationResults.Restored ? TenancyAuditEvents.AuditChainRestored : TenancyAuditEvents.AuditChainBroken;
            await audit.WriteAsync(new AuditEvent(eventType, AuditOutcome.Failure, actor.Id, verification.TenantSlug, details, "tenant", null, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (verification.Result == AuditVerificationResults.Passed)
        {
            logger.LogInformation("The audit chain of tenant {Slug} verified from position {From} to {To}: {Events} events.", verification.TenantSlug, verification.FromPosition, verification.ToPosition, verification.EventsChecked);
            return;
        }

        AuditChainMetrics.VerificationFailures.Add(1, new KeyValuePair<string, object?>("result", verification.Result));
        foreach (var problem in verification.Problems.Take(20))
        {
            logger.LogError("ALERT: the audit chain of tenant {Slug} {Result} at position {Position}, event {EventId}: {Message}", verification.TenantSlug, verification.Result, problem.Position, problem.EventId, problem.Message);
        }

        if (verification.RestorePosition is { } point)
        {
            logger.LogError("ALERT: the audit chain of tenant {Slug} matches its anchors up to position {Position}, sealed at {At}: the restore point.", verification.TenantSlug, point, verification.RestoreAt);
        }
    }

    public static AuditVerificationInfo Info(AuditVerificationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var problems = JsonSerializer.Deserialize<List<AuditChainProblemInfo>>(record.Problems, Json) ?? [];
        return new AuditVerificationInfo(record.TenantSlug, record.JobId, record.Full, record.FromPosition, record.ToPosition, record.ToHash, record.Result, record.EventsChecked, problems, record.RestorePosition, record.RestoreAt, record.CreatedAt) { Id = record.Id };
    }

    private async Task<AuditAnchorInfo?> LatestAnchorAsync(string slug, string kind, CancellationToken cancellationToken)
    {
        var latest = await context.AuditAnchors.AsNoTracking()
            .Where(a => a.TenantSlug == slug && a.Kind == kind)
            .OrderByDescending(a => a.Position)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return latest is null ? null : Info(latest);
    }

    private static AuditAnchorInfo Info(AuditAnchorRecord record) => new(record.Kind, record.Position, record.Hash, record.At, record.CreatedAt, record.Segment) { Id = record.Id };

    private static AuditRestoreInfo Info(AuditRestoreRecord record) =>
        new(record.Id, record.TenantSlug, record.Segment, record.StartPosition, record.PreviousHash, record.RestorePosition, record.RestoreAt, record.LostFrom, record.LostTo, record.Ticket, record.Reason, record.AcceptedByName, record.CreatedAt);

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}

/// <summary>What a unit reads to resume sending its anchors, and to learn of restores operators accepted (ADR-0050).</summary>
public sealed record UnitChainState(AuditAnchorInfo? Seal, AuditAnchorInfo? Pruned, int Segment, IReadOnlyList<AuditRestoreInfo> Restores);

/// <summary>The control plane's measures of the tenants' chains (ADR-0050), exported with every <c>Iron.*</c> meter.</summary>
public static class AuditChainMetrics
{
    public static readonly Meter Meter = new("Iron.AuditChains");

    /// <summary>Verifications that did not pass, by result: failed or restored.</summary>
    public static readonly Counter<long> VerificationFailures = Meter.CreateCounter<long>("audit.verification_failures");

    /// <summary>Anchors sent for a position anchored before with another hash.</summary>
    public static readonly Counter<long> AnchorConflicts = Meter.CreateCounter<long>("audit.anchor_conflicts");
}
