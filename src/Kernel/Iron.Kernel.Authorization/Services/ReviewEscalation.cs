using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Services;

/// <summary>The kernel's escalation (ADR-0037): it knows no managers, so it names the person who started the campaign.</summary>
public sealed class CampaignOwnerEscalation : IReviewEscalation
{
    public Task<IReadOnlyList<Guid>> TargetsAsync(OverdueReview review, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);
        return Task.FromResult<IReadOnlyList<Guid>>([review.OwnerId]);
    }
}

/// <summary>
/// The daily pass over open campaigns (TF-038 AC3): every unit of a campaign 7 days past its due date with items still
/// open is escalated once, to the targets <see cref="IReviewEscalation"/> names, with a row and an audit event in one
/// transaction. Sending the message is S4; until then the row, the event and the campaign page are the report.
/// </summary>
public sealed class ReviewEscalationPass(
    AuthorizationDbContext context,
    IReviewEscalation escalation,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal current,
    TimeProvider time,
    ILogger<ReviewEscalationPass> logger)
{
    /// <summary>How late reviewers are before someone else hears of it.</summary>
    public const int DaysLate = 7;

    /// <summary>Runs once; answers how many units it escalated. Safe to run again: a unit is escalated once per campaign.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var latest = today.AddDays(-DaysLate);
        var reviews = await context.AccessReviews.AsNoTracking()
            .Where(r => r.Status == ReviewStatus.Open && r.DueOn <= latest)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var escalated = 0;
        foreach (var review in reviews)
        {
            var done = await context.AccessReviewEscalations.AsNoTracking().Where(e => e.ReviewId == review.Id).Select(e => e.UnitId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var open = await context.AccessReviewItems.AsNoTracking()
                .Where(i => i.ReviewId == review.Id && !done.Contains(i.UnitId) && !context.AccessReviewDecisions.Any(d => d.ItemId == i.Id))
                .GroupBy(i => new { i.UnitId, i.UnitName })
                .Select(g => new { g.Key.UnitId, g.Key.UnitName, Count = g.Count() })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var unit in open)
            {
                if (await EscalateAsync(review, unit.UnitId, unit.UnitName, unit.Count, today, cancellationToken).ConfigureAwait(false))
                {
                    escalated++;
                }
            }
        }

        if (escalated > 0)
        {
            logger.LogInformation("Escalated {Count} unit(s) of access reviews {Days} or more days overdue.", escalated, DaysLate);
        }

        return escalated;
    }

    /// <summary>
    /// The reviewers of a unit: the principals holding <c>authz.reviews.decide</c> there, tenant-wide, at the unit, or
    /// with children at the unit or above it. <paramref name="unitPath"/> is the ids from the root to the unit.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ReviewersAsync(IReadOnlyList<Guid> unitPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitPath);
        var unit = unitPath[^1];
        var path = unitPath.ToArray();
        var deciding = context.Roles.Where(r => r.Permissions.Contains(AuthorizationPermissions.ReviewsDecide.Name)).Select(r => r.Id);
        return await context.RoleAssignments.AsNoTracking()
            .Where(a => deciding.Contains(a.RoleId)
                && (a.ScopeKind == Scopes.Tenant
                    || (a.ScopeKind == Scopes.Unit && a.ScopeId == unit)
                    || (a.ScopeKind == Scopes.UnitWithChildren && path.Contains(a.ScopeId!.Value))))
            .Select(a => a.PrincipalId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> EscalateAsync(AccessReview review, Guid unitId, string unitName, int openItems, DateOnly today, CancellationToken cancellationToken)
    {
        var path = await context.AccessReviewItems.AsNoTracking().Where(i => i.ReviewId == review.Id && i.UnitId == unitId).Select(i => i.UnitPathIds).FirstAsync(cancellationToken).ConfigureAwait(false);
        var reviewers = await ReviewersAsync(path, cancellationToken).ConfigureAwait(false);
        var daysOverdue = review.DaysOverdue(today);
        var overdue = new OverdueReview(review.Id, review.Name, review.OwnerId, unitId, unitName, review.DueOn, daysOverdue, openItems, reviewers);
        var named = await escalation.TargetsAsync(overdue, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Guid> targets = named.Count == 0 ? [review.OwnerId] : named;

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({ReviewService.LockKey(review.Id)})", cancellationToken).ConfigureAwait(false);
        if (await context.AccessReviewEscalations.AnyAsync(e => e.ReviewId == review.Id && e.UnitId == unitId, cancellationToken).ConfigureAwait(false))
        {
            return false; // another worker of this unit escalated it meanwhile
        }

        var row = new AccessReviewEscalation(review.Id, unitId, openItems, daysOverdue, reviewers, targets);
        context.AccessReviewEscalations.Add(row);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["access_review_id"] = review.Id.ToString("D"),
            ["unit_id"] = unitId.ToString("D"),
            ["due_on"] = review.DueOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["days_overdue"] = daysOverdue.ToString(CultureInfo.InvariantCulture),
            ["open_items"] = openItems.ToString(CultureInfo.InvariantCulture),
            ["reviewers"] = string.Join(',', row.Reviewers.Select(r => r.ToString("D"))),
            ["targets"] = string.Join(',', row.Targets.Select(t => t.ToString("D"))),
        };
        await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.ReviewEscalated, AuditOutcome.Success, current.Id, unitName, details, ReviewService.RecordType, review.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// Runs the escalation pass in the unit's worker (TF-038 AC3): a minute after the worker starts, so a restart catches up,
/// then once a day. A failed pass is logged and tried again the next day; nothing is lost, since the pass looks at the
/// campaigns as they are.
/// </summary>
public sealed class ReviewEscalationWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<ReviewEscalationWorker> logger) : BackgroundService
{
    public static readonly TimeSpan FirstRunAfter = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunAfter, time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ReviewEscalationPass>().RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The access review escalation pass failed; it runs again in {Interval}.", Interval);
        }
    }
}
