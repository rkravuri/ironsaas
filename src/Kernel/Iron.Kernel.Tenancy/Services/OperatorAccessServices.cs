using System.Globalization;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>The operators by name, as the control plane's Identity knows them; their id where no directory is hosted.</summary>
public sealed class OperatorNames(IServiceProvider services)
{
    public async Task<string> OfAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        var directory = services.GetService<IPrincipals>();
        var found = directory is null ? null : await directory.FindAsync([operatorId], cancellationToken).ConfigureAwait(false);
        return found is not null && found.TryGetValue(operatorId, out var summary) ? summary.DisplayName : operatorId.ToString("D");
    }
}

/// <summary>
/// The operator's support access (TF-025, ADR-0049): a one-time link to sign in as a person of a tenant that allows it,
/// with a reason and a duration, for an operator with a fresh token. The fleet runs the unit's <c>impersonate</c> command;
/// the provisioning report gives the link once to the operator who asked. Both planes' trails name the operator.
/// </summary>
public sealed class OperatorImpersonation(TenancyDbContext context, IProvisioningJobs jobs, IUnitOfWork unitOfWork, IAuditWriter audit, ICurrentPrincipal actor, OperatorNames names, ILogger<OperatorImpersonation> logger)
{
    public async Task<Result<ProvisioningJobInfo>> RequestAsync(string slug, string loginId, string reason, int? minutes, CancellationToken cancellationToken)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why))
        {
            return Error.Validation(TenancyErrors.ImpersonationReasonRequired, "Say why you sign in as this person: they and the tenant's administrators read it.", new FieldError("reason", "required", "Say why."));
        }

        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return Error.Conflict(TenancyErrors.NotProvisionable, $"Tenant '{slug}' is {tenant.Status.ToString().ToLowerInvariant()}; only an active tenant's people can be signed in as.");
        }

        var name = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Impersonation__LoginId"] = loginId.Trim().ToLowerInvariant(),
            ["Impersonation__OperatorId"] = actor.Id.ToString("D"),
            ["Impersonation__Operator"] = name,
            ["Impersonation__Reason"] = why,
        };
        if (minutes is { } chosen)
        {
            environment["Impersonation__Minutes"] = chosen.ToString(CultureInfo.InvariantCulture);
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var job = await jobs.RequestAsync(tenant.Slug, JobKind.Impersonate, new JobRequest(loginId.Trim().ToLowerInvariant(), why, name) { Environment = environment }, cancellationToken).ConfigureAwait(false);
        if (job.IsFailure)
        {
            return job.Error;
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["person"] = loginId.Trim().ToLowerInvariant(), ["operator"] = name, ["job_id"] = job.Value.Id.ToString("D") };
        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.ImpersonationRequested, AuditOutcome.Success, actor.Id, tenant.Slug, details, "tenant", tenant.Id, why, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Operator {OperatorId} asked to sign in as a person of tenant {Slug}: job {JobId}.", actor.Id, tenant.Slug, job.Value.Id);
        return job.Value;
    }
}

/// <summary>A break-glass access as the control plane shows it, and the sign-in link once to the operator who asked.</summary>
public sealed record BreakGlassView(BreakGlassAccess Access, JobHandover? Link, HandoverState LinkState);

/// <summary>
/// Break-glass (TF-026, ADR-0049): an operator asks with a reason, a ticket and a duration; a second, different operator
/// approves with a fresh token within 30 minutes, or the request expires; the fleet opens the unit's account and hands its
/// link once to the operator who asked; at the time limit, or when an operator ends it, the fleet closes the account and
/// the review of every action comes back here. Every step is in the control plane's audit trail; the unit's names both
/// operators, the ticket and the reason.
/// </summary>
public sealed class BreakGlassService(
    TenancyDbContext context,
    IProvisioningJobs jobs,
    IJobHandovers handovers,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    OperatorNames names,
    TimeProvider time,
    ILogger<BreakGlassService> logger)
{
    public async Task<Result<BreakGlassAccess>> RequestAsync(string slug, string reason, string ticket, int? minutes, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return Error.Conflict(TenancyErrors.NotProvisionable, $"Tenant '{slug}' is {tenant.Status.ToString().ToLowerInvariant()}; break-glass reaches only an active tenant.");
        }

        var duration = minutes ?? BreakGlassAccess.DefaultMinutes;
        if (duration is < 1 or > BreakGlassAccess.LongestMinutes)
        {
            return Error.Validation(TenancyErrors.BreakGlassDuration, $"Choose from 1 to {BreakGlassAccess.LongestMinutes} minutes.", new FieldError("minutes", "out_of_range", $"From 1 to {BreakGlassAccess.LongestMinutes} minutes."));
        }

        var access = new BreakGlassAccess(tenant.Slug, reason, ticket, duration, actor.Id, await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false), time.GetUtcNow());
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.BreakGlassAccesses.Add(access);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(TenancyAuditEvents.BreakGlassRequested, access, access.Reason, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Break-glass {Id} requested for tenant {Slug} by {Operator}, ticket {Ticket}; it waits for a second operator until {Deadline}.", access.Id, access.TenantSlug, access.RequestedByName, access.Ticket, access.ApprovalDeadline);
        return access;
    }

    /// <summary>
    /// The second operator approves (TF-026 AC1): not the one who asked (AC4), within 30 minutes (AC2), with a fresh token
    /// (the endpoint asks). The fleet's job opens the account; its link goes to the operator who asked.
    /// </summary>
    public async Task<Result<BreakGlassAccess>> ApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        var access = await context.BreakGlassAccesses.SingleOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
        if (access is null)
        {
            return Error.NotFound(TenancyErrors.BreakGlassNotFound, "There is no break-glass request with this id.");
        }

        if (access.RequestedById == actor.Id)
        {
            return Error.Forbidden(TenancyErrors.BreakGlassSameOperator, "You asked for this access, so you cannot approve it: a second operator does.");
        }

        if (access.Status == BreakGlassStatus.Requested && time.GetUtcNow() >= access.ApprovalDeadline)
        {
            await ExpireAsync(access, cancellationToken).ConfigureAwait(false);
        }

        if (access.Status != BreakGlassStatus.Requested)
        {
            return Error.Conflict(TenancyErrors.BreakGlassNotRequested, $"This request is {access.Status.ToString().ToLowerInvariant()}, so it cannot be approved. A request waits 30 minutes for its second operator.");
        }

        var approver = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var until = now.AddMinutes(access.Minutes);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BreakGlass__Id"] = access.Id.ToString("D"),
            ["BreakGlass__Operator"] = access.RequestedByName,
            ["BreakGlass__Approver"] = approver,
            ["BreakGlass__Ticket"] = access.Ticket,
            ["BreakGlass__Reason"] = access.Reason,
            ["BreakGlass__Until"] = until.ToString("O", CultureInfo.InvariantCulture),
        };
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var job = await jobs.RequestAsync(access.TenantSlug, JobKind.BreakGlassOpen, new JobRequest(access.AccountLoginId, access.Reason, access.RequestedByName) { Environment = environment, HandOverTo = access.RequestedById }, cancellationToken).ConfigureAwait(false);
        if (job.IsFailure)
        {
            return job.Error;
        }

        access.Approve(actor.Id, approver, now, job.Value.Id);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(TenancyAuditEvents.BreakGlassApproved, access, access.Reason, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Break-glass {Id} for tenant {Slug} approved by {Approver}; open until {Until}.", access.Id, access.TenantSlug, approver, until);
        return access;
    }

    /// <summary>An operator ends the access early: the fleet closes the account and the review follows, as at the time limit.</summary>
    public async Task<Result<BreakGlassAccess>> EndAsync(Guid id, CancellationToken cancellationToken)
    {
        var access = await context.BreakGlassAccesses.SingleOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
        if (access is null)
        {
            return Error.NotFound(TenancyErrors.BreakGlassNotFound, "There is no break-glass request with this id.");
        }

        if (access.Status != BreakGlassStatus.Active)
        {
            return Error.Conflict(TenancyErrors.BreakGlassNotActive, $"This access is {access.Status.ToString().ToLowerInvariant()}; only an active one can be ended.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        access.Close($"ended early by {await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false)}");
        await RequestCloseAsync(access, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(TenancyAuditEvents.BreakGlassEnding, access, access.EndReason, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return access;
    }

    /// <summary>One access, and its sign-in link once to the operator who asked, after the account is open.</summary>
    public async Task<Result<BreakGlassView>> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var access = await context.BreakGlassAccesses.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
        if (access is null)
        {
            return Error.NotFound(TenancyErrors.BreakGlassNotFound, "There is no break-glass request with this id.");
        }

        if (access.Status == BreakGlassStatus.Active && access.OpenJobId is { } openJob)
        {
            var answer = await handovers.TakeAsync(openJob, actor.Id, cancellationToken).ConfigureAwait(false);
            return new BreakGlassView(access, answer.Item, answer.State);
        }

        return new BreakGlassView(access, null, HandoverState.None);
    }

    /// <summary>
    /// The fleet host's minute (TF-026 AC2, AC3): requests past their 30 minutes expire; accesses past their time limit
    /// close; a close waits for the unit to be free, then its job; a completed close's review is collected and kept; an
    /// open that failed is marked so. Each change is audited.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var changed = 0;
        var open = await context.BreakGlassAccesses
            .Where(a => a.Status == BreakGlassStatus.Requested || a.Status == BreakGlassStatus.Active || a.Status == BreakGlassStatus.Ending)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var access in open)
        {
            if (access.Status == BreakGlassStatus.Requested && now >= access.ApprovalDeadline)
            {
                await ExpireAsync(access, cancellationToken).ConfigureAwait(false);
                changed++;
                continue;
            }

            if (access.Status == BreakGlassStatus.Active && access.OpenJobId is { } openJob
                && await jobs.FindAsync(openJob, cancellationToken).ConfigureAwait(false) is { Status: ProvisioningStatus.Failed } failed)
            {
                access.Fail($"the unit could not open the account: {failed.Error}");
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await AuditAsync(TenancyAuditEvents.BreakGlassFailed, access, access.EndReason, cancellationToken).ConfigureAwait(false);
                changed++;
                continue;
            }

            if (access.Status == BreakGlassStatus.Active && now >= access.AccessUntil)
            {
                access.Close("time limit reached");
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await AuditAsync(TenancyAuditEvents.BreakGlassEnding, access, access.EndReason, cancellationToken).ConfigureAwait(false);
                changed++;
            }

            if (access.Status == BreakGlassStatus.Ending)
            {
                changed += await AdvanceCloseAsync(access, now, cancellationToken).ConfigureAwait(false);
            }
        }

        return changed;
    }

    private async Task<int> AdvanceCloseAsync(BreakGlassAccess access, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (access.CloseJobId is not { } closeJob)
        {
            return await RequestCloseAsync(access, cancellationToken).ConfigureAwait(false) ? 1 : 0;
        }

        var job = await jobs.FindAsync(closeJob, cancellationToken).ConfigureAwait(false);
        if (job is { Status: ProvisioningStatus.Failed })
        {
            // The close is safe to run again: the next minute asks the fleet afresh.
            access.Closing(null);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogWarning("The close of break-glass {Id} failed ({Error}); it is asked again.", access.Id, job.Error);
            return 1;
        }

        if (job is not { Status: ProvisioningStatus.Completed } || await handovers.CollectAsync(closeJob, cancellationToken).ConfigureAwait(false) is not { } item)
        {
            return 0;
        }

        var review = item.Payload ?? "[]";
        var actions = JsonDocument.Parse(review).RootElement.GetArrayLength();
        access.Reviewed(review, actions, now);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(TenancyAuditEvents.BreakGlassEnded, access, access.EndReason, cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Break-glass {Id} for tenant {Slug} ended ({Reason}); its review lists {Actions} action(s).", access.Id, access.TenantSlug, access.EndReason, actions);
        return 1;
    }

    /// <summary>Asks the fleet to close the account; while another job of the tenant runs, the next minute asks again.</summary>
    private async Task<bool> RequestCloseAsync(BreakGlassAccess access, CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BreakGlass__Id"] = access.Id.ToString("D"),
            ["BreakGlass__Account"] = access.AccountLoginId,
            ["BreakGlass__Reason"] = access.EndReason ?? "ended",
        };
        var job = await jobs.RequestAsync(access.TenantSlug, JobKind.BreakGlassClose, new JobRequest(access.AccountLoginId, access.EndReason ?? "ended", access.RequestedByName) { Environment = environment }, cancellationToken).ConfigureAwait(false);
        if (job.IsFailure)
        {
            logger.LogInformation("The close of break-glass {Id} waits: {Message}", access.Id, job.Error.Message);
            return false;
        }

        access.Closing(job.Value.Id);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ExpireAsync(BreakGlassAccess access, CancellationToken cancellationToken)
    {
        access.Expire();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(TenancyAuditEvents.BreakGlassExpired, access, "no second operator approved within 30 minutes", cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Break-glass {Id} for tenant {Slug} expired: no second operator approved within 30 minutes.", access.Id, access.TenantSlug);
    }

    private Task AuditAsync(string eventType, BreakGlassAccess access, string? reason, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = access.Status.ToString().ToLowerInvariant(),
            ["ticket"] = access.Ticket,
            ["operator"] = access.RequestedByName,
            ["account"] = access.AccountLoginId,
        };
        if (access.ApprovedByName is { } approver)
        {
            details["approver"] = approver;
        }

        if (access.ReviewActions is { } actions)
        {
            details["actions"] = actions.ToString(CultureInfo.InvariantCulture);
        }

        return audit.WriteAsync(new AuditEvent(eventType, AuditOutcome.Success, actor.Id, access.TenantSlug, details, "break_glass", access.Id, reason, Severity: AuditSeverity.High), cancellationToken);
    }
}

/// <summary>
/// The platform's documents (TF-028, ADR-0049): the operator publishes a new version of the terms of service or the
/// privacy notice; every unit reads the current ones from the store within a minute and asks everyone to accept them.
/// The store is written before the row is committed, so a version the units do not have is never shown as published.
/// </summary>
public sealed class PlatformDocumentService(TenancyDbContext context, ISecretStore store, IUnitOfWork unitOfWork, IAuditWriter audit, ICurrentPrincipal actor, TimeProvider time)
{
    public async Task<IReadOnlyList<PlatformDocument>> CurrentAsync(CancellationToken cancellationToken)
    {
        var all = await context.PlatformDocuments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.GroupBy(d => d.Key).Select(g => g.MaxBy(d => d.Number)!).OrderBy(d => d.Key, StringComparer.Ordinal)];
    }

    public async Task<Result<PlatformDocument>> PublishAsync(string key, string title, string body, CancellationToken cancellationToken)
    {
        if (!PlatformDocumentKeys.All.Contains(key, StringComparer.Ordinal))
        {
            return Error.Validation(TenancyErrors.DocumentKeyUnknown, $"The platform's documents are {string.Join(" and ", PlatformDocumentKeys.All)}.", new FieldError("key", "unknown", "Choose one of the platform's documents."));
        }

        var latest = await context.PlatformDocuments.Where(d => d.Key == key).MaxAsync(d => (int?)d.Number, cancellationToken).ConfigureAwait(false) ?? 0;
        var document = new PlatformDocument(key, latest + 1, title, body, time.GetUtcNow());
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.PlatformDocuments.Add(document);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["key"] = key, ["version"] = document.Number.ToString(CultureInfo.InvariantCulture), ["title"] = document.Title };
        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.PlatformDocumentPublished, AuditOutcome.Success, actor.Id, key, details, "platform_document", document.Id), cancellationToken).ConfigureAwait(false);
        var current = await CurrentWithinAsync(document, cancellationToken).ConfigureAwait(false);
        await store.WriteAsync(PlatformDocumentsStore.Path, new Dictionary<string, string>(StringComparer.Ordinal) { [PlatformDocumentsStore.DocumentKey] = PlatformDocumentsStore.ToJson(current) }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <summary>The current versions with the new one among them, read inside the transaction that adds it.</summary>
    private async Task<IReadOnlyList<PublishedDocument>> CurrentWithinAsync(PlatformDocument added, CancellationToken cancellationToken)
    {
        var all = await context.PlatformDocuments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (all.All(d => d.Id != added.Id))
        {
            all.Add(added);
        }

        return [.. all.GroupBy(d => d.Key).Select(g => g.MaxBy(d => d.Number)!).OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => new PublishedDocument(d.Key, d.Number, d.Title, d.Body, d.PublishedAt))];
    }
}

/// <summary>The fleet host's minute for break-glass (ADR-0049): expiries, time limits, closes and reviews.</summary>
public sealed class BreakGlassWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<BreakGlassWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BreakGlassService>().ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The break-glass minute failed; it runs again in a minute.");
            }

            await Task.Delay(Every, time, stoppingToken).ConfigureAwait(false);
        }
    }
}
