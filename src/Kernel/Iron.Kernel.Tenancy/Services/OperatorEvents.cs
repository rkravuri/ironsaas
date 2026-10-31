using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>What the operator searches a tenant's events by: a type, a record, a request's correlation id, or the dead letters only.</summary>
public sealed record OperatorEventSearch(string? Type, Guid? RecordId, string? CorrelationId, bool DeadOnly);

/// <summary>A replay the operator asks of a tenant: to one subscriber, of named events or a range of positions, with why.</summary>
public sealed record OperatorEventReplay(string Subscriber, IReadOnlyList<Guid>? EventIds, long? FromPosition, long? ToPosition, string Reason);

/// <summary>
/// The operator's search and replay of a tenant's domain events (TF-112, ADR-0060): the fleet runs the unit's
/// <c>events</c> command with what was asked, and the job's output is the answer. A replay needs a reason, which both
/// trails keep with the operator's name; a search is recorded too, since it reads a tenant's events.
/// </summary>
public sealed class OperatorEvents(TenancyDbContext context, IProvisioningJobs jobs, IUnitOfWork unitOfWork, IAuditWriter audit, ICurrentPrincipal actor, OperatorNames names)
{
    public Task<Result<ProvisioningJobInfo>> SearchAsync(string slug, OperatorEventSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["Events__Command__Action"] = "search" };
        if (search.Type is { Length: > 0 } type)
        {
            environment["Events__Command__Type"] = type;
        }

        if (search.RecordId is { } record)
        {
            environment["Events__Command__RecordId"] = record.ToString("D");
        }

        if (search.CorrelationId is { Length: > 0 } correlation)
        {
            environment["Events__Command__CorrelationId"] = correlation;
        }

        if (search.DeadOnly)
        {
            environment["Events__Command__Status"] = "dead";
        }

        return RequestAsync(slug, "search", null, environment, TenancyAuditEvents.EventsSearched, AuditSeverity.Normal, cancellationToken);
    }

    public Task<Result<ProvisioningJobInfo>> ReplayAsync(string slug, OperatorEventReplay replay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var why = replay.Reason?.Trim();
        if (string.IsNullOrEmpty(why))
        {
            return Task.FromResult<Result<ProvisioningJobInfo>>(Error.Validation(TenancyErrors.EventsReasonRequired, "Say why you replay the tenant's events: both audit trails keep it.", new FieldError("reason", "required", "Say why.")));
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Events__Command__Action"] = "replay",
            ["Events__Command__Subscriber"] = replay.Subscriber,
            ["Events__Command__Reason"] = why,
        };
        if (replay.EventIds is { Count: > 0 } ids)
        {
            environment["Events__Command__Ids"] = string.Join(',', ids.Select(id => id.ToString("D")));
        }

        if (replay.FromPosition is { } from)
        {
            environment["Events__Command__FromPosition"] = from.ToString(CultureInfo.InvariantCulture);
        }

        if (replay.ToPosition is { } to)
        {
            environment["Events__Command__ToPosition"] = to.ToString(CultureInfo.InvariantCulture);
        }

        return RequestAsync(slug, replay.Subscriber, why, environment, TenancyAuditEvents.EventsReplayRequested, AuditSeverity.High, cancellationToken);
    }

    private async Task<Result<ProvisioningJobInfo>> RequestAsync(string slug, string subject, string? reason, Dictionary<string, string> environment, string action, string severity, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return Error.Conflict(TenancyErrors.NotProvisionable, $"Tenant '{slug}' is {tenant.Status.ToString().ToLowerInvariant()}; only an active tenant's events can be searched or replayed.");
        }

        var name = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        environment["Events__Command__Operator"] = name;
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var job = await jobs.RequestAsync(tenant.Slug, JobKind.Events, new JobRequest(subject, reason ?? "search", name) { Environment = environment }, cancellationToken).ConfigureAwait(false);
        if (job.IsFailure)
        {
            return job.Error;
        }

        var details = environment.Where(e => e.Key != "Events__Command__Reason").ToDictionary(e => e.Key["Events__Command__".Length..].ToLowerInvariant(), e => e.Value, StringComparer.Ordinal);
        details["job_id"] = job.Value.Id.ToString("D");
        await audit.WriteAsync(new AuditEvent(action, AuditOutcome.Success, actor.Id, tenant.Slug, details, "tenant", tenant.Id, reason, Severity: severity), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job.Value;
    }
}
