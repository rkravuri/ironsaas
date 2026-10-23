using System.Globalization;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Files.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Audit.Services;

/// <summary>
/// The operator's limits per retention class (<c>Retention:Limits:&lt;code&gt;:MinimumDays</c> and <c>MaximumDays</c>, ADR-0050):
/// a deployment may narrow the platform's, never widen them below the class's own minimum. The fleet puts them into
/// each unit's secret from its own configuration.
/// </summary>
public sealed class RetentionLimitsOptions
{
    public const string SectionName = "Retention";

    public Dictionary<string, RetentionLimit> Limits { get; set; } = new(StringComparer.Ordinal);

    public sealed class RetentionLimit
    {
        public int? MinimumDays { get; set; }

        public int? MaximumDays { get; set; }
    }
}

/// <summary>One class as the tenant sees it: its default, the operator's limits, its days now, and whether the tenant chose them.</summary>
public sealed record RetentionClassView(RetentionClass Class, int MinimumDays, int MaximumDays, int Days, bool Chosen)
{
    /// <summary>The tenant's choice for the class, whose history the Retention page shows; null while it has none.</summary>
    public Guid? ChoiceId { get; init; }
}

/// <summary>
/// How long each class keeps its records in this tenant (TF-066, ADR-0050): the tenant's choice within the operator's
/// limits, or the class's default. A choice below the minimum or above the maximum is refused (AC3); a choice the limits
/// later exclude is held to them, so a narrowed limit applies at once.
/// </summary>
public sealed class RetentionPolicy(AuditDbContext context, RetentionLimitsOptions limits, IUnitOfWork unitOfWork, IAuditWriter audit, ICurrentPrincipal actor, IFourEyes? fourEyes = null) : IRetentionPeriods
{
    /// <summary>The kind of change a retention choice becomes while it waits for a second administrator (TF-068, ADR-0052).</summary>
    public const string ChooseKind = "audit.retention.choose";

    /// <summary>A class's retention as a pending change shows and compares it: the days kept, and whether the tenant chose them.</summary>
    public async Task<JsonElement> StateAsync(string code, CancellationToken cancellationToken)
    {
        var retention = RetentionClasses.All.Single(c => c.Code == code);
        var choice = await context.RetentionChoices.AsNoTracking().SingleOrDefaultAsync(c => c.ClassCode == code, cancellationToken).ConfigureAwait(false);
        return ChangeJson.Of(new { Class = code, Days = choice?.Days ?? retention.DefaultDays, Chosen = choice is not null });
    }

    public (int Minimum, int Maximum) LimitsOf(RetentionClass retention)
    {
        ArgumentNullException.ThrowIfNull(retention);
        var set = limits.Limits.GetValueOrDefault(retention.Code);
        var minimum = Math.Max(retention.MinimumDays, set?.MinimumDays ?? retention.MinimumDays);
        var maximum = Math.Max(minimum, Math.Min(retention.MaximumDays, set?.MaximumDays ?? retention.MaximumDays));
        return (minimum, maximum);
    }

    public async Task<int> DaysAsync(RetentionClass retention, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retention);
        var (minimum, maximum) = LimitsOf(retention);
        var chosen = await context.RetentionChoices.AsNoTracking().Where(c => c.ClassCode == retention.Code).Select(c => (int?)c.Days).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return Math.Clamp(chosen ?? retention.DefaultDays, minimum, maximum);
    }

    public async Task<IReadOnlyList<RetentionClassView>> ClassesAsync(CancellationToken cancellationToken)
    {
        var choices = await context.RetentionChoices.AsNoTracking().ToDictionaryAsync(c => c.ClassCode, cancellationToken).ConfigureAwait(false);
        return [.. RetentionClasses.All.Select(retention =>
        {
            var (minimum, maximum) = LimitsOf(retention);
            var chosen = choices.TryGetValue(retention.Code, out var choice);
            return new RetentionClassView(retention, minimum, maximum, Math.Clamp(chosen ? choice!.Days : retention.DefaultDays, minimum, maximum), chosen) { ChoiceId = choice?.Id };
        })];
    }

    public async Task<Result<RetentionClassView>> ChooseAsync(string code, int days, string? reason, CancellationToken cancellationToken)
    {
        var retention = RetentionClasses.All.SingleOrDefault(c => c.Code == code);
        if (retention is null)
        {
            return Error.NotFound(AuditErrors.RetentionClassUnknown, $"There is no retention class '{code}'.");
        }

        var (minimum, maximum) = LimitsOf(retention);
        if (days < minimum)
        {
            return Error.Validation(AuditErrors.RetentionBelowMinimum, $"{retention.Description} are kept at least {minimum} days: the platform's operator sets that minimum.", new FieldError("days", "below_minimum", $"Choose {minimum} days or more."));
        }

        if (days > maximum)
        {
            return Error.Validation(AuditErrors.RetentionAboveMaximum, $"{retention.Description} are kept at most {maximum} days.", new FieldError("days", "above_maximum", $"Choose {maximum} days or fewer."));
        }

        // A retention choice waits for a second administrator when the tenant lists retention for four-eyes (TF-068 AC1).
        if (fourEyes is not null && await fourEyes.AppliesAsync(ConfigurationAreas.Retention, cancellationToken).ConfigureAwait(false))
        {
            var proposal = new ChangeProposal(
                ConfigurationAreas.Retention,
                ChooseKind,
                $"retention:{code}",
                $"Keep {retention.Description.ToLowerInvariant()} for {days} days",
                await StateAsync(code, cancellationToken).ConfigureAwait(false),
                ChangeJson.Of(new { Class = code, Days = days, Chosen = true }),
                ChangeJson.Of(new RetentionChangePayload(code, days)),
                AuditPermissions.RetentionManage.Name,
                AccessScope.Tenant,
                reason);
            return await fourEyes.HoldAsync(proposal, cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var choice = await context.RetentionChoices.SingleOrDefaultAsync(c => c.ClassCode == code, cancellationToken).ConfigureAwait(false);
        var before = choice?.Days ?? retention.DefaultDays;
        if (choice is null)
        {
            choice = new RetentionChoice(code, days);
            context.RetentionChoices.Add(choice);
        }
        else
        {
            choice.Change(days);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["class"] = code,
            ["before_days"] = before.ToString(CultureInfo.InvariantCulture),
            ["after_days"] = days.ToString(CultureInfo.InvariantCulture),
        };
        await audit.WriteAsync(new AuditEvent(AuditEvents.RetentionChanged, AuditOutcome.Success, actor.Id, code, details, "retention_class", null, reason), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RetentionClassView(retention, minimum, maximum, days, true) { ChoiceId = choice.Id };
    }
}

/// <summary>
/// The legal holds (TF-066 AC2, ADR-0050): on the whole tenant, one record, or every record linked to a master. Placing
/// and releasing need a reason, step-up (on the endpoints) and are audited at high severity; a released hold stays as a
/// record of when and why.
/// </summary>
public sealed class LegalHolds(AuditDbContext context, IUnitOfWork unitOfWork, IAuditWriter audit, ICurrentPrincipal actor, TimeProvider time, IFourEyes? fourEyes = null) : ILegalHolds
{
    /// <summary>The kinds of change placing and releasing become while they wait for a second administrator (TF-068, ADR-0052).</summary>
    public const string PlaceKind = "audit.legal_hold.place";

    public const string ReleaseKind = "audit.legal_hold.release";

    /// <summary>
    /// What a held placing or release compares (ADR-0052): for a placing, a standing hold on the same target or nothing;
    /// for a release, the hold with whether it is released.
    /// </summary>
    public async Task<JsonElement> StateAsync(LegalHoldChangePayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var hold = payload.HoldId is { } id
            ? await context.LegalHolds.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, cancellationToken).ConfigureAwait(false)
            : await context.LegalHolds.AsNoTracking().FirstOrDefaultAsync(h => h.ReleasedAt == null && h.Scope == payload.Scope && h.RecordType == payload.RecordType && h.RecordId == payload.RecordId, cancellationToken).ConfigureAwait(false);
        return hold is null ? ChangeJson.Nothing : State(hold);
    }

    public static JsonElement State(LegalHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return ChangeJson.Of(new { Hold = hold.Id, hold.Scope, hold.RecordType, hold.RecordId, hold.Reason, Released = hold.ReleasedAt is not null });
    }

    private async Task<Error?> HeldAsync(string kind, string subject, string title, JsonElement before, JsonElement after, LegalHoldChangePayload payload, string reason, CancellationToken cancellationToken)
    {
        if (fourEyes is null || !await fourEyes.AppliesAsync(ConfigurationAreas.LegalHolds, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var proposal = new ChangeProposal(ConfigurationAreas.LegalHolds, kind, subject, title, before, after, ChangeJson.Of(payload), AuditPermissions.LegalHoldsManage.Name, AccessScope.Tenant, reason);
        return await fourEyes.HoldAsync(proposal, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HeldRecords> ActiveAsync(CancellationToken cancellationToken)
    {
        var active = await context.LegalHolds.AsNoTracking().Where(h => h.ReleasedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new HeldRecords(
            active.Any(h => h.Scope == LegalHoldScopes.Tenant),
            active.Where(h => h.Scope == LegalHoldScopes.Record && h.RecordType is not null && h.RecordId is not null).Select(h => (h.RecordType!, h.RecordId!.Value)).ToHashSet(),
            active.Where(h => h.Scope == LegalHoldScopes.Master && h.RecordType is not null && h.RecordId is not null).Select(h => (h.RecordType!, h.RecordId!.Value)).ToHashSet());
    }

    public async Task<Result<LegalHold>> PlaceAsync(string scope, string? recordType, Guid? recordId, string reason, CancellationToken cancellationToken)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why))
        {
            return Error.Validation(AuditErrors.LegalHoldReasonRequired, "Say why the hold is placed: the auditors read it.", new FieldError("reason", "required", "Say why."));
        }

        var named = scope == LegalHoldScopes.Tenant ? recordType is null && recordId is null : !string.IsNullOrWhiteSpace(recordType) && recordId is not null;
        if (scope is not (LegalHoldScopes.Tenant or LegalHoldScopes.Record or LegalHoldScopes.Master) || !named)
        {
            return Error.Validation(AuditErrors.LegalHoldScopeInvalid, "A hold is on the whole tenant, or on one record or master named by its type and id.");
        }

        var placing = new LegalHoldChangePayload(null, scope, recordType?.Trim(), recordId, why);
        var on = scope == LegalHoldScopes.Tenant ? "the whole tenant" : $"{placing.RecordType} {recordId:D}";
        if (await HeldAsync(PlaceKind, $"legal_hold:new:{scope}:{placing.RecordType}:{recordId:N}", $"Place a legal hold on {on}", ChangeJson.Nothing, ChangeJson.Of(new { Scope = scope, placing.RecordType, RecordId = recordId, Reason = why, Released = false }), placing, why, cancellationToken).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var hold = new LegalHold(scope, recordType?.Trim(), recordId, why);
        context.LegalHolds.Add(hold);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["scope"] = scope, ["hold_id"] = hold.Id.ToString("D") };
        await audit.WriteAsync(new AuditEvent(AuditEvents.LegalHoldPlaced, AuditOutcome.Success, actor.Id, Target(hold), details, hold.RecordType, hold.RecordId, why, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return hold;
    }

    public async Task<Result<LegalHold>> ReleaseAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why))
        {
            return Error.Validation(AuditErrors.LegalHoldReasonRequired, "Say why the hold is released: the auditors read it.", new FieldError("reason", "required", "Say why."));
        }

        var found = await context.LegalHolds.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return Error.NotFound(AuditErrors.LegalHoldNotFound, "There is no such legal hold.");
        }

        if (!found.Active)
        {
            return Error.Conflict(AuditErrors.LegalHoldReleased, "This hold was released already.");
        }

        var releasing = new LegalHoldChangePayload(found.Id, found.Scope, found.RecordType, found.RecordId, why);
        var on = found.Scope == LegalHoldScopes.Tenant ? "the whole tenant" : $"{found.RecordType} {found.RecordId:D}";
        if (await HeldAsync(ReleaseKind, $"legal_hold:{found.Id:N}", $"Release the legal hold on {on}", State(found), ChangeJson.Of(new { Hold = found.Id, found.Scope, found.RecordType, found.RecordId, found.Reason, Released = true }), releasing, why, cancellationToken).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var hold = await context.LegalHolds.SingleOrDefaultAsync(h => h.Id == id, cancellationToken).ConfigureAwait(false);
        if (hold is null)
        {
            return Error.NotFound(AuditErrors.LegalHoldNotFound, "There is no such legal hold.");
        }

        if (!hold.Active)
        {
            return Error.Conflict(AuditErrors.LegalHoldReleased, "This hold was released already.");
        }

        hold.Release(actor.Id, why, time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["scope"] = hold.Scope, ["hold_id"] = hold.Id.ToString("D") };
        await audit.WriteAsync(new AuditEvent(AuditEvents.LegalHoldReleased, AuditOutcome.Success, actor.Id, Target(hold), details, hold.RecordType, hold.RecordId, why, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return hold;
    }

    private static string Target(LegalHold hold) => hold.Scope == LegalHoldScopes.Tenant ? "the whole tenant" : $"{hold.RecordType} {hold.RecordId:D}";
}

/// <summary>A record's copy before the retention job deletes it (TF-066 AC1): JSON in the tenant's container, under its type and id.</summary>
public sealed class BlobRecordArchive(IObjectStorage storage, TimeProvider time) : IRecordArchive
{
    public async Task<string> ArchiveAsync(string recordType, RetentionClass retention, RetainedRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(record);
        var key = string.Create(CultureInfo.InvariantCulture, $"retention/{recordType}/{record.RetainedFrom:yyyy-MM}/{record.Id:D}.json");
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["record_type"] = recordType,
            ["id"] = record.Id,
            ["retention_class"] = retention.Code,
            ["retained_from"] = record.RetainedFrom,
            ["archived_at"] = time.GetUtcNow(),
            ["master_type"] = record.MasterType,
            ["master_id"] = record.MasterId,
            ["content"] = JsonDocument.Parse(record.Content).RootElement.Clone(),
        };
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(copy)));
        await storage.PutAsync(key, stream, "application/json", cancellationToken).ConfigureAwait(false);
        return key;
    }
}

/// <summary>Each record the retention job deleted, as an audit event in the deletion's transaction (TF-066 AC1).</summary>
public sealed class RetentionAuditWriter(IAuditWriter audit) : IRetentionAudit
{
    public Task ExpiredAsync(RecordExpired expired, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expired);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["retention_class"] = expired.RetentionClass,
            ["retained_from"] = AuditCanonicalForm.Time(expired.RetainedFrom),
            ["days"] = expired.Days.ToString(CultureInfo.InvariantCulture),
            ["archive_key"] = expired.ArchiveKey,
        };
        return audit.WriteAsync(new AuditEvent(AuditEvents.RecordExpired, AuditOutcome.Success, SystemPrincipal.SystemId, expired.RecordType, details, expired.RecordType, expired.RecordId, "retention"), cancellationToken);
    }
}

/// <summary>
/// The storage of a host without the Files module, such as the control plane (ADR-0050): it keeps no files, so the archive,
/// the record copies and the exports say so rather than failing at start. A host with Files registers the real one after it.
/// </summary>
public sealed class NoObjectStorage : IObjectStorage
{
    private const string Reason = "This host keeps no files: the archive, record copies and exports run in a tenant's unit.";

    public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) => throw new InvalidOperationException(Reason);

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException(Reason);

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException(Reason);

    public Task EnsureContainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Reason);
}
