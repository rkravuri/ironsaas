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
    UnitReviewers reviewersOf,
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

    private async Task<bool> EscalateAsync(AccessReview review, Guid unitId, string unitName, int openItems, DateOnly today, CancellationToken cancellationToken)
    {
        var path = await context.AccessReviewItems.AsNoTracking().Where(i => i.ReviewId == review.Id && i.UnitId == unitId).Select(i => i.UnitPathIds).FirstAsync(cancellationToken).ConfigureAwait(false);
        var reviewers = await reviewersOf.OfUnitAsync(path, cancellationToken).ConfigureAwait(false);
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
/// <c>authz.escalate_reviews</c> (TF-038 AC3, TF-124, ADR-0061): the escalation of late access reviews, a minute after a
/// unit first schedules it, then once a day. Nothing is lost by a failed run, retried by its policy, since the pass looks
/// at the campaigns as they are; a repeated run escalates nothing twice.
/// </summary>
public sealed class ReviewEscalationJob(ReviewEscalationPass pass) : IJob
{
    public static readonly JobDefinition Definition = JobDefinition.Create<ReviewEscalationJob>(
        "authz.escalate_reviews", "Escalates the access reviews whose reviewers are late.", "Platform team",
        JobSchedule.Interval(TimeSpan.FromDays(1), firstAfter: TimeSpan.FromMinutes(1)), PriorityClass.Normal, TimeSpan.FromHours(1));

    public async Task<Result> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        await pass.RunAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
