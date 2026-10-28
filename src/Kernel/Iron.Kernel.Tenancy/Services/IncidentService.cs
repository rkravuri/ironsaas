using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>A tenant the operator names as affected: whether its personal data is, and since when that is known.</summary>
public sealed record AffectedTenant(string Tenant, bool PersonalData, DateTimeOffset? KnownAt);

/// <summary>What an operator declares (TF-079 AC2).</summary>
public sealed record IncidentDeclaration(int Severity, string Summary, string Lead, string? Communications, string? Scribe, IReadOnlyList<AffectedTenant> Tenants);

/// <summary>An incident with its tenants and its timeline, oldest first.</summary>
public sealed record IncidentRecord(Incident Incident, IReadOnlyList<IncidentTenant> Tenants, IReadOnlyList<IncidentEntry> Entries);

/// <summary>
/// Incidents in the control plane (TF-079 AC2, ADR-0056), run by the runbook (<c>docs/security/incident-response.md</c>):
/// an operator declares one with a severity, the tenants affected, a lead and a summary; the timeline keeps what was done
/// and the communication log what was said; each tenant whose personal data was affected has 72 hours from when that
/// became known to be told, and the incident does not close while one is not; at the close the post-incident review is
/// due within 10 days. Every step is an audit event of the control plane, with the operator.
/// </summary>
public sealed class IncidentService(
    TenancyDbContext context,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    OperatorNames names,
    TimeProvider time)
{
    public const int MinimumSeverity = 1;
    public const int MaximumSeverity = 4;

    /// <summary>How far back a reported moment of knowing may lie; an older one is likely a mistake of date.</summary>
    public static readonly TimeSpan KnownAtMostAgo = TimeSpan.FromDays(30);

    public async Task<Result<IncidentRecord>> DeclareAsync(IncidentDeclaration declaration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var now = time.GetUtcNow();
        if (declaration.Severity is < MinimumSeverity or > MaximumSeverity)
        {
            return Error.Validation(TenancyErrors.IncidentInvalid, "Give the incident a severity from 1, the most severe, to 4.", new FieldError("severity", "invalid", "Choose 1, 2, 3 or 4."));
        }

        if (string.IsNullOrWhiteSpace(declaration.Summary) || string.IsNullOrWhiteSpace(declaration.Lead))
        {
            return Error.Validation(TenancyErrors.IncidentInvalid, "An incident needs a summary and its lead.", new FieldError(string.IsNullOrWhiteSpace(declaration.Lead) ? "lead" : "summary", "required", "Say what happened and who leads."));
        }

        foreach (var affected in declaration.Tenants)
        {
            if (await AffectedProblemAsync(affected, now, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return problem;
            }
        }

        var by = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        var incident = new Incident(declaration.Severity, declaration.Summary.Trim(), declaration.Lead.Trim(), Blank(declaration.Communications), Blank(declaration.Scribe), now, actor.Id);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.Incidents.Add(incident);
        context.IncidentEntries.Add(new IncidentEntry(incident.Id, now, actor.Id, by, IncidentEntry.Declared, $"Declared at severity {declaration.Severity.ToString(CultureInfo.InvariantCulture)}, led by {incident.Lead}: {incident.Summary}"));
        foreach (var affected in declaration.Tenants.DistinctBy(t => t.Tenant, StringComparer.Ordinal))
        {
            context.IncidentTenants.Add(new IncidentTenant(incident.Id, affected.Tenant, affected.PersonalData, affected.PersonalData ? affected.KnownAt ?? now : null));
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["severity"] = declaration.Severity.ToString(CultureInfo.InvariantCulture),
            ["lead"] = incident.Lead,
            ["tenants"] = string.Join(',', declaration.Tenants.Select(t => t.Tenant)),
            ["personal_data"] = declaration.Tenants.Any(t => t.PersonalData) ? "true" : "false",
        };
        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.IncidentDeclared, AuditOutcome.Success, actor.Id, incident.Summary, details, RecordType, incident.Id, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await RecordAsync(incident.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A timeline entry, a communication, or the notice to a tenant whose personal data was affected.</summary>
    public async Task<Result<IncidentRecord>> NoteAsync(Guid id, string kind, string text, string? tenant, CancellationToken cancellationToken)
    {
        if (kind is not (IncidentEntry.Note or IncidentEntry.Communication or IncidentEntry.NoticeSent))
        {
            return Error.Validation(TenancyErrors.IncidentInvalid, "An entry is a note, a communication or a notice.", new FieldError("kind", "unknown", "Choose note, communication or notice."));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Error.Validation(TenancyErrors.IncidentInvalid, "Say what happened, what was said, or the notice you sent.", new FieldError("text", "required", "Write the entry."));
        }

        var incident = await context.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return NotFound();
        }

        IncidentTenant? affected = null;
        if (tenant is not null || kind == IncidentEntry.NoticeSent)
        {
            affected = await context.IncidentTenants.SingleOrDefaultAsync(t => t.IncidentId == id && t.TenantSlug == tenant, cancellationToken).ConfigureAwait(false);
            if (affected is null)
            {
                return Error.Validation(TenancyErrors.IncidentTenantNotAffected, $"The incident does not name the tenant {tenant ?? "(none)"}. Add it as affected first.", new FieldError("tenant", "not_affected", "Name a tenant the incident affects."));
            }

            if (kind == IncidentEntry.NoticeSent && !affected.PersonalData)
            {
                return Error.Validation(TenancyErrors.IncidentInvalid, $"The tenant {tenant}'s personal data is not affected, so no breach notice is due. Record what you told it as a communication.", new FieldError("kind", "not_due", "Choose communication."));
            }
        }

        var now = time.GetUtcNow();
        var by = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.IncidentEntries.Add(new IncidentEntry(id, now, actor.Id, by, kind, text.Trim(), tenant));
        if (kind == IncidentEntry.NoticeSent)
        {
            affected!.Notified(now, actor.Id, text.Trim());
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = kind };
        if (tenant is not null)
        {
            details["tenant"] = tenant;
        }

        if (affected?.NoticeDueAt is { } due && kind == IncidentEntry.NoticeSent)
        {
            details["notice_due_at"] = due.ToString("O", CultureInfo.InvariantCulture);
            details["on_time"] = now < due ? "true" : "false";
        }

        await audit.WriteAsync(new AuditEvent(kind == IncidentEntry.NoticeSent ? TenancyAuditEvents.IncidentNoticeSent : TenancyAuditEvents.IncidentNoted, AuditOutcome.Success, actor.Id, incident.Summary, details, RecordType, id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await RecordAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A tenant found affected, or its personal data found affected: its deadline runs from <see cref="AffectedTenant.KnownAt"/>.</summary>
    public async Task<Result<IncidentRecord>> AffectAsync(Guid id, AffectedTenant affected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(affected);
        var incident = await context.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return NotFound();
        }

        var now = time.GetUtcNow();
        if (await AffectedProblemAsync(affected, now, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return problem;
        }

        var by = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var kept = await context.IncidentTenants.SingleOrDefaultAsync(t => t.IncidentId == id && t.TenantSlug == affected.Tenant, cancellationToken).ConfigureAwait(false);
        var knownAt = affected.PersonalData ? affected.KnownAt ?? kept?.KnownAt ?? now : (DateTimeOffset?)null;
        if (kept is null)
        {
            context.IncidentTenants.Add(new IncidentTenant(id, affected.Tenant, affected.PersonalData, knownAt));
        }
        else
        {
            kept.Affect(affected.PersonalData, knownAt);
        }

        var text = affected.PersonalData
            ? $"The tenant {affected.Tenant}'s personal data is affected, known since {knownAt!.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}: tell it by {(knownAt.Value + IncidentTenant.NoticeWithin).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}."
            : $"The tenant {affected.Tenant} is affected; its personal data is not.";
        context.IncidentEntries.Add(new IncidentEntry(id, now, actor.Id, by, IncidentEntry.TenantAffected, text, affected.Tenant));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = affected.Tenant, ["personal_data"] = affected.PersonalData ? "true" : "false" };
        if (knownAt is { } known)
        {
            details["known_at"] = known.ToString("O", CultureInfo.InvariantCulture);
        }

        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.IncidentTenantAffected, AuditOutcome.Success, actor.Id, incident.Summary, details, RecordType, id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await RecordAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IncidentRecord>> CloseAsync(Guid id, string summary, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return Error.Validation(TenancyErrors.IncidentInvalid, "Say how the incident ended.", new FieldError("summary", "required", "Say how it ended."));
        }

        var incident = await context.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return NotFound();
        }

        if (incident.Status == Incident.Closed)
        {
            return Error.Conflict(TenancyErrors.IncidentClosed, "The incident is closed already.");
        }

        var untold = await context.IncidentTenants.AsNoTracking().Where(t => t.IncidentId == id && t.PersonalData && t.NotifiedAt == null).Select(t => t.TenantSlug).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (untold.Count > 0)
        {
            return Error.Conflict(TenancyErrors.IncidentNoticeMissing, $"Tell every tenant whose personal data was affected before you close the incident: {string.Join(", ", untold.Order(StringComparer.Ordinal))} has no notice yet.");
        }

        var now = time.GetUtcNow();
        var by = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        incident.Close(now, summary.Trim());
        context.IncidentEntries.Add(new IncidentEntry(id, now, actor.Id, by, IncidentEntry.ClosedKind, $"Closed: {incident.CloseSummary} The review is due by {incident.ReviewDueAt!.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}."));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["review_due_at"] = incident.ReviewDueAt!.Value.ToString("O", CultureInfo.InvariantCulture) };
        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.IncidentClosed, AuditOutcome.Success, actor.Id, incident.Summary, details, RecordType, id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await RecordAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The post-incident review, once the incident is closed, once (TF-079 AC2).</summary>
    public async Task<Result<IncidentRecord>> ReviewAsync(Guid id, string whatHappened, string why, string whatChanges, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(whatHappened) || string.IsNullOrWhiteSpace(why) || string.IsNullOrWhiteSpace(whatChanges))
        {
            return Error.Validation(TenancyErrors.IncidentInvalid, "A review says what happened, why, and what changes.", new FieldError(string.IsNullOrWhiteSpace(whatHappened) ? "what_happened" : string.IsNullOrWhiteSpace(why) ? "why" : "what_changes", "required", "Write each of the three."));
        }

        var incident = await context.Incidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return NotFound();
        }

        if (incident.Status != Incident.Closed)
        {
            return Error.Conflict(TenancyErrors.IncidentOpen, "Close the incident first: its review looks back at all of it.");
        }

        if (incident.ReviewRecordedAt is not null)
        {
            return Error.Conflict(TenancyErrors.IncidentReviewRecorded, "The review is recorded already.");
        }

        var now = time.GetUtcNow();
        var by = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        incident.Review(now, actor.Id, whatHappened.Trim(), why.Trim(), whatChanges.Trim());
        // On time is before the deadline: at the deadline itself the escalation is due.
        var onTime = now < incident.ReviewDueAt;
        context.IncidentEntries.Add(new IncidentEntry(id, now, actor.Id, by, IncidentEntry.ReviewKind, onTime ? "The post-incident review is recorded, within its 10 days." : "The post-incident review is recorded, after its 10 days."));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["on_time"] = onTime ? "true" : "false" };
        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.IncidentReviewRecorded, AuditOutcome.Success, actor.Id, incident.Summary, details, RecordType, id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await RecordAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IncidentRecord>> RecordAsync(Guid id, CancellationToken cancellationToken)
    {
        var incident = await context.Incidents.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return NotFound();
        }

        var tenants = await context.IncidentTenants.AsNoTracking().Where(t => t.IncidentId == id).OrderBy(t => t.TenantSlug).ToListAsync(cancellationToken).ConfigureAwait(false);
        var entries = await context.IncidentEntries.AsNoTracking().Where(e => e.IncidentId == id).OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new IncidentRecord(incident, tenants, entries);
    }

    public const string RecordType = "incident";

    private static Error NotFound() => Error.NotFound(TenancyErrors.IncidentNotFound, "There is no such incident.");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<Error?> AffectedProblemAsync(AffectedTenant affected, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await context.Tenants.AsNoTracking().AnyAsync(t => t.Slug == affected.Tenant, cancellationToken).ConfigureAwait(false))
        {
            return Error.Validation(TenancyErrors.TenantNotFound, $"There is no tenant {affected.Tenant}.", new FieldError("tenants", "unknown", $"Name tenants that exist; {affected.Tenant} does not."));
        }

        if (affected.KnownAt is { } known && (known > now || known < now - KnownAtMostAgo))
        {
            return Error.Validation(TenancyErrors.IncidentInvalid, "When the breach became known is a moment of the last 30 days, not in the future.", new FieldError("known_at", "invalid", "Give when it became known, up to now."));
        }

        return null;
    }
}

/// <summary>
/// The incidents' deadlines (TF-079 AC2, ADR-0056), every few minutes in the control plane: a closed incident whose review
/// is not recorded 10 days after the close, and a tenant whose personal data was affected and who is not told 72 hours
/// after it became known, are each escalated once: an entry of the timeline, an audit event at high severity, which the
/// control plane's alert rules raise to the operator's alerting, and an <c>ALERT:</c> line.
/// </summary>
public sealed class IncidentEscalation(TenancyDbContext context, IUnitOfWork unitOfWork, IAuditWriter audit, TimeProvider time, ILogger<IncidentEscalation> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var reviews = await context.Incidents
            .Where(i => i.Status == Incident.Closed && i.ReviewRecordedAt == null && i.ReviewEscalatedAt == null && i.ReviewDueAt <= now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var notices = await context.IncidentTenants
            .Where(t => t.PersonalData && t.NotifiedAt == null && t.NoticeEscalatedAt == null && t.KnownAt != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        notices = [.. notices.Where(t => t.NoticeDueAt <= now)];
        if (reviews.Count == 0 && notices.Count == 0)
        {
            return 0;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        foreach (var incident in reviews)
        {
            incident.ReviewEscalated(now);
            context.IncidentEntries.Add(new IncidentEntry(incident.Id, now, SystemPrincipal.SystemId, "The control plane", IncidentEntry.Escalated, $"The post-incident review was due by {Format(incident.ReviewDueAt!.Value)} and is not recorded: escalated."));
            var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["review_due_at"] = incident.ReviewDueAt.Value.ToString("O", CultureInfo.InvariantCulture), ["lead"] = incident.Lead };
            await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.IncidentReviewOverdue, AuditOutcome.Failure, SystemPrincipal.SystemId, incident.Summary, details, IncidentService.RecordType, incident.Id, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
            logger.LogError("ALERT: the post-incident review of incident {IncidentId}, led by {Lead}, was due by {Due} and is not recorded.", incident.Id, incident.Lead, incident.ReviewDueAt);
        }

        foreach (var tenant in notices)
        {
            tenant.NoticeEscalated(now);
            context.IncidentEntries.Add(new IncidentEntry(tenant.IncidentId, now, SystemPrincipal.SystemId, "The control plane", IncidentEntry.Escalated, $"The tenant {tenant.TenantSlug} was to be told of the breach by {Format(tenant.NoticeDueAt!.Value)} and is not: escalated.", tenant.TenantSlug));
            var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = tenant.TenantSlug, ["notice_due_at"] = tenant.NoticeDueAt.Value.ToString("O", CultureInfo.InvariantCulture) };
            await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.IncidentNoticeOverdue, AuditOutcome.Failure, SystemPrincipal.SystemId, tenant.TenantSlug, details, IncidentService.RecordType, tenant.IncidentId, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
            logger.LogError("ALERT: the tenant {Tenant} was to be told of the breach of incident {IncidentId} by {Due} and is not.", tenant.TenantSlug, tenant.IncidentId, tenant.NoticeDueAt);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reviews.Count + notices.Count;
    }

    private static string Format(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}

/// <summary>The incidents' deadlines every five minutes, in the control plane.</summary>
public sealed class IncidentEscalationWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<IncidentEscalationWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IncidentEscalation>().RunAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "The incidents' deadlines could not be checked; they are checked again in {Interval}.", Interval);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
