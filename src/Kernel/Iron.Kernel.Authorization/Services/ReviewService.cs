using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>A unit a campaign is to cover, alone or with every unit below it.</summary>
public sealed record ReviewUnitInput(Guid UnitId, bool IncludeChildren);

public sealed record StartReviewInput(string Name, DateOnly DueOn, IReadOnlyList<ReviewUnitInput> Units);

public sealed record ReviewPerson(Guid Id, string Name);

/// <summary>How far the deciding is: every item is decided or open; an open item after the due date is overdue too.</summary>
public sealed record ReviewProgress(int Total, int Decided, int Confirmed, int Revoked, int Open, int Overdue);

public sealed record ReviewUnitView(Guid UnitId, string Name, bool IncludesChildren);

public sealed record EscalationView(DateTimeOffset At, int DaysOverdue, int OpenItems, IReadOnlyList<ReviewPerson> Reviewers, IReadOnlyList<ReviewPerson> Targets);

/// <summary>One unit's share of a campaign, for the reader: its path, its progress, and its escalation when there was one.</summary>
public sealed record ReviewUnitProgress(Guid UnitId, string UnitName, IReadOnlyList<string> UnitPath, ReviewProgress Progress, EscalationView? Escalation);

/// <summary>
/// A campaign as its reader sees it. A tenant-wide manager sees the whole campaign; a reviewer sees the progress of their
/// own units only, since the other units' items are not theirs to see (TF-038 AC1).
/// </summary>
public sealed record ReviewView(
    Guid Id,
    string Name,
    DateOnly DueOn,
    ReviewStatus Status,
    bool Overdue,
    ReviewPerson Owner,
    IReadOnlyList<ReviewUnitView> Units,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt,
    ReviewProgress Progress,
    IReadOnlyList<ReviewUnitProgress> ByUnit,
    bool CanManage);

public sealed record ItemDecisionView(ReviewDecision Decision, string? Comment, ReviewPerson DecidedBy, DateTimeOffset DecidedAt, bool AssignmentRemoved);

/// <summary>One item to decide: the assignment as it stood when the campaign started, and the decision once there is one.</summary>
public sealed record ReviewItemView(
    Guid Id,
    Guid ReviewId,
    Guid AssignmentId,
    Guid PrincipalId,
    string PrincipalName,
    Guid RoleId,
    string RoleCode,
    string RoleName,
    RoleScopeKind ScopeKind,
    Guid UnitId,
    string UnitName,
    IReadOnlyList<UnitStep> UnitPath,
    ItemDecisionView? Decision);

/// <summary>What a confirm-all did: the items it confirmed, and those someone had decided already, which it left alone.</summary>
public sealed record ConfirmManyView(int Confirmed, int AlreadyDecided);

/// <summary>Which items a list answers: those still to decide, those decided, or both.</summary>
public enum ItemState
{
    All,
    Open,
    Decided,
}

/// <summary>
/// Access review campaigns (TF-038, ADR-0037). Starting and closing one needs <c>authz.reviews.manage</c> tenant-wide;
/// the endpoint checks it. A reviewer is whoever holds <c>authz.reviews.decide</c> at an item's unit: they see and decide
/// the items of their units and no others. A revoke removes the assignment through the assignments' own removal path,
/// in the transaction that records the decision, so the last-administrator guard holds and the change is at once.
/// </summary>
public sealed class ReviewService(
    AuthorizationDbContext context,
    AccessGrants grants,
    AssignmentService assignments,
    IPrincipals principals,
    IPermissionEvaluator evaluator,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal current,
    TimeProvider time)
{
    public const string RecordType = "access_review";

    /// <summary>At most this many items in one confirm-all: a page of the list, never the whole campaign unseen.</summary>
    public const int ConfirmManyLimit = 200;

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>
    /// The campaigns the reader may see: every one for a tenant-wide manager; for a reviewer, those with items at their
    /// units; null for anyone else, who is refused (TF-038 AC4).
    /// </summary>
    public async Task<IQueryable<AccessReview>?> VisibleAsync(CancellationToken cancellationToken)
    {
        var held = await grants.OfAsync(current.Id, cancellationToken).ConfigureAwait(false);
        if (held.TenantWide.Contains(AuthorizationPermissions.ReviewsManage.Name))
        {
            return context.AccessReviews.AsNoTracking();
        }

        if (!held.Anywhere.Contains(AuthorizationPermissions.ReviewsDecide.Name))
        {
            return null;
        }

        var items = await DecidableAsync(cancellationToken).ConfigureAwait(false);
        return context.AccessReviews.AsNoTracking().Where(review => items.Any(item => item.ReviewId == review.Id));
    }

    /// <summary>The refusal for a reader who neither manages nor reviews: the denial is audited like any other.</summary>
    public async Task<Error> RefusalAsync(string operation, CancellationToken cancellationToken)
    {
        await evaluator.EvaluateAsync(new AccessRequest(current.Id, AuthorizationPermissions.ReviewsDecide, operation, operation) { Scope = AccessScope.Anywhere }, cancellationToken).ConfigureAwait(false);
        return Error.Forbidden(PermissionErrors.PermissionDenied, $"Access reviews are for the people who start them ({AuthorizationPermissions.ReviewsManage.Name}) and their reviewers ({AuthorizationPermissions.ReviewsDecide.Name}). You hold neither.");
    }

    /// <summary>Starts a campaign over the units (TF-038): the snapshot of their assignments is taken now, in one transaction with the audit event.</summary>
    public async Task<Result<ReviewView>> StartAsync(StartReviewInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Units.Count == 0)
        {
            return Error.Validation(AuthorizationErrors.ReviewUnitsRequired, "A campaign covers at least one unit.", new FieldError("units", "required", "Choose the units to review."));
        }

        if (input.DueOn < Today)
        {
            return Error.Validation(AuthorizationErrors.ReviewDuePassed, "The due date has passed.", new FieldError("due_on", "passed", "Choose today or a later day."));
        }

        var unitIds = input.Units.Select(u => u.UnitId).Distinct().ToArray();
        var chosen = await context.OrganisationUnits.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (chosen.Count != unitIds.Length)
        {
            return Error.Validation(AuthorizationErrors.UnitNotFound, "One of the units does not exist.", new FieldError("units", "not_found", "Choose existing units."));
        }

        var covered = await CoveredAsync(input.Units, chosen, cancellationToken).ConfigureAwait(false);
        var rootCovered = covered.Values.Any(u => u.ParentId is null);
        var coveredIds = covered.Keys.ToArray();

        // Unit assignments at a covered unit, and the tenant-wide ones when the root is covered. A record assignment
        // belongs to its record, not to a unit's people, and is left to the record's own review (ADR-0037).
        var rows = await context.RoleAssignments.AsNoTracking()
            .Where(a => ((a.ScopeKind == Scopes.Unit || a.ScopeKind == Scopes.UnitWithChildren) && coveredIds.Contains(a.ScopeId!.Value)) || (rootCovered && a.ScopeKind == Scopes.Tenant))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var roleIds = rows.Select(r => r.RoleId).Distinct().ToArray();
        var roles = await context.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken).ConfigureAwait(false);
        var people = await principals.FindAsync([.. rows.Select(r => r.PrincipalId).Distinct()], cancellationToken).ConfigureAwait(false);
        var root = covered.Values.FirstOrDefault(u => u.ParentId is null);
        var names = await NamesAsync(covered.Values.SelectMany(u => OrganisationUnit.PathIds(u.Path)), cancellationToken).ConfigureAwait(false);

        var review = new AccessReview(input.Name, input.DueOn, current.Id);
        var items = new List<AccessReviewItem>();
        foreach (var row in rows)
        {
            var unit = row.ScopeKind == Scopes.Tenant ? root! : covered[row.ScopeId!.Value];
            var path = OrganisationUnit.PathIds(unit.Path);
            var name = people.TryGetValue(row.PrincipalId, out var person) ? person.DisplayName : row.PrincipalId.ToString("D");
            items.Add(new AccessReviewItem(review.Id, row, name, roles[row.RoleId], path, [.. path.Select(id => names.GetValueOrDefault(id, string.Empty))]));
        }

        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            context.AccessReviews.Add(review);
            context.AccessReviewUnits.AddRange(input.Units.DistinctBy(u => u.UnitId).Select(u => new AccessReviewUnit(review.Id, u.UnitId, u.IncludeChildren)));
            context.AccessReviewItems.AddRange(items);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = review.Name,
                ["due_on"] = review.DueOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["units"] = string.Join(',', input.Units.Select(u => u.IncludeChildren ? $"{u.UnitId:D}+children" : u.UnitId.ToString("D"))),
                ["items"] = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.ReviewStarted, AuditOutcome.Success, current.Id, review.Name, details, RecordType, review.Id), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await DescribeAsync(review.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One campaign the reader may see, with the progress of what they may see.</summary>
    public async Task<Result<ReviewView>> DescribeAsync(Guid id, CancellationToken cancellationToken)
    {
        var visible = await VisibleAsync(cancellationToken).ConfigureAwait(false);
        var review = visible is null ? null : await visible.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (review is null)
        {
            return Error.NotFound(AuthorizationErrors.ReviewNotFound, "There is no access review with this id that you may see.");
        }

        var manages = await ManagesAsync(cancellationToken).ConfigureAwait(false);
        var items = manages ? context.AccessReviewItems.AsNoTracking() : await DecidableAsync(cancellationToken).ConfigureAwait(false);
        var rows = await (
                from item in items.Where(i => i.ReviewId == id)
                join made in context.AccessReviewDecisions on item.Id equals made.ItemId into decisions
                from made in decisions.DefaultIfEmpty()
                select new { item.UnitId, item.UnitName, Decision = made == null ? (ReviewDecision?)null : made.Decision })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var paths = await items.Where(i => i.ReviewId == id).Select(i => new { i.UnitId, i.UnitPathNames }).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var pathOf = paths.GroupBy(p => p.UnitId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.First().UnitPathNames);
        var escalations = await context.AccessReviewEscalations.AsNoTracking().Where(e => e.ReviewId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var units = await context.AccessReviewUnits.AsNoTracking().Where(u => u.ReviewId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var unitNames = await NamesAsync(units.Select(u => u.UnitId), cancellationToken).ConfigureAwait(false);
        var people = await principals.FindAsync([.. escalations.SelectMany(e => e.Reviewers.Concat(e.Targets)).Append(review.OwnerId).Distinct()], cancellationToken).ConfigureAwait(false);
        ReviewPerson Person(Guid person) => new(person, people.TryGetValue(person, out var found) ? found.DisplayName : person.ToString("D"));

        var overdue = review.IsOpen && review.DaysOverdue(Today) > 0;
        ReviewProgress Progress(IEnumerable<ReviewDecision?> decisions)
        {
            var list = decisions.ToList();
            var open = list.Count(d => d is null);
            return new ReviewProgress(list.Count, list.Count - open, list.Count(d => d == ReviewDecision.Confirm), list.Count(d => d == ReviewDecision.Revoke), open, overdue ? open : 0);
        }

        var byUnit = rows.GroupBy(r => (r.UnitId, r.UnitName))
            .Select(g =>
            {
                var escalation = escalations.FirstOrDefault(e => e.UnitId == g.Key.UnitId);
                return new ReviewUnitProgress(
                    g.Key.UnitId,
                    g.Key.UnitName,
                    pathOf.GetValueOrDefault(g.Key.UnitId, [g.Key.UnitName]),
                    Progress(g.Select(r => r.Decision)),
                    escalation is null ? null : new EscalationView(escalation.CreatedAt, escalation.DaysOverdue, escalation.OpenItems, [.. escalation.Reviewers.Select(Person)], [.. escalation.Targets.Select(Person)]));
            })
            .OrderBy(u => string.Join('/', u.UnitPath), StringComparer.Ordinal)
            .ToList();

        return new ReviewView(
            review.Id,
            review.Name,
            review.DueOn,
            review.Status,
            overdue,
            Person(review.OwnerId),
            [.. units.Select(u => new ReviewUnitView(u.UnitId, unitNames.GetValueOrDefault(u.UnitId, string.Empty), u.IncludesChildren)).OrderBy(u => u.Name, StringComparer.Ordinal)],
            review.CreatedAt,
            review.ClosedAt,
            Progress(rows.Select(r => r.Decision)),
            byUnit,
            manages);
    }

    /// <summary>The progress of each campaign of a page, counted over what the reader may see, in two queries.</summary>
    public async Task<IReadOnlyDictionary<Guid, ReviewProgress>> ProgressAsync(IReadOnlyList<AccessReview> reviews, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        var ids = reviews.Select(r => r.Id).ToArray();
        var items = await ManagesAsync(cancellationToken).ConfigureAwait(false) ? context.AccessReviewItems.AsNoTracking() : await DecidableAsync(cancellationToken).ConfigureAwait(false);
        var counts = await (
                from item in items.Where(i => ids.Contains(i.ReviewId))
                join made in context.AccessReviewDecisions on item.Id equals made.ItemId into decisions
                from made in decisions.DefaultIfEmpty()
                group item by new { item.ReviewId, Decision = made == null ? (ReviewDecision?)null : made.Decision } into counted
                select new { counted.Key.ReviewId, counted.Key.Decision, Count = counted.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var today = Today;
        return reviews.ToDictionary(r => r.Id, r =>
        {
            var mine = counts.Where(c => c.ReviewId == r.Id).ToList();
            var confirmed = mine.Where(c => c.Decision == ReviewDecision.Confirm).Sum(c => c.Count);
            var revoked = mine.Where(c => c.Decision == ReviewDecision.Revoke).Sum(c => c.Count);
            var open = mine.Where(c => c.Decision is null).Sum(c => c.Count);
            return new ReviewProgress(confirmed + revoked + open, confirmed + revoked, confirmed, revoked, open, r.IsOpen && r.DaysOverdue(today) > 0 ? open : 0);
        });
    }

    /// <summary>The items of a campaign the reader decides: at the units where they hold <c>authz.reviews.decide</c>.</summary>
    public async Task<IQueryable<AccessReviewItem>> ItemsAsync(Guid reviewId, ItemState state, CancellationToken cancellationToken)
    {
        var items = (await DecidableAsync(cancellationToken).ConfigureAwait(false)).Where(i => i.ReviewId == reviewId);
        return state switch
        {
            ItemState.Open => items.Where(i => !context.AccessReviewDecisions.Any(d => d.ItemId == i.Id)),
            ItemState.Decided => items.Where(i => context.AccessReviewDecisions.Any(d => d.ItemId == i.Id)),
            _ => items,
        };
    }

    /// <summary>The items with their decisions and the deciders' names: a few queries for the page.</summary>
    public async Task<IReadOnlyList<ReviewItemView>> DescribeItemsAsync(IReadOnlyList<AccessReviewItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ids = items.Select(i => i.Id).ToArray();
        var decisions = await context.AccessReviewDecisions.AsNoTracking().Where(d => ids.Contains(d.ItemId)).ToDictionaryAsync(d => d.ItemId, cancellationToken).ConfigureAwait(false);
        var people = await principals.FindAsync([.. decisions.Values.Select(d => d.CreatedBy).Distinct()], cancellationToken).ConfigureAwait(false);
        return [.. items.Select(item =>
        {
            ItemDecisionView? decision = decisions.TryGetValue(item.Id, out var made)
                ? new ItemDecisionView(made.Decision, made.Comment, new ReviewPerson(made.CreatedBy, people.TryGetValue(made.CreatedBy, out var by) ? by.DisplayName : made.CreatedBy.ToString("D")), made.CreatedAt, made.AssignmentRemoved)
                : null;
            return new ReviewItemView(
                item.Id,
                item.ReviewId,
                item.AssignmentId,
                item.PrincipalId,
                item.PrincipalName,
                item.RoleId,
                item.RoleCode,
                item.RoleName,
                Scopes.KindOf(item.ScopeKind),
                item.UnitId,
                item.UnitName,
                [.. item.UnitPathIds.Zip(item.UnitPathNames, (unit, name) => new UnitStep(unit, name))],
                decision);
        })];
    }

    /// <summary>The unit that owns an item, for the endpoint's permission check at the item's unit.</summary>
    public static async Task<Guid?> ItemUnitAsync(AuthorizationDbContext context, Guid itemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await context.AccessReviewItems.AsNoTracking().Where(i => i.Id == itemId).Select(i => (Guid?)i.UnitId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Decides one item (TF-038 AC2). A revoke removes the assignment at once, in the transaction that records the
    /// decision; when the guard keeps the last tenant administrator, nothing is recorded and the answer says why.
    /// </summary>
    public async Task<Result<ReviewItemView>> DecideAsync(Guid reviewId, Guid itemId, ReviewDecision decision, string? comment, CancellationToken cancellationToken)
    {
        var item = await (await ItemsAsync(reviewId, ItemState.All, cancellationToken).ConfigureAwait(false)).SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Error.NotFound(AuthorizationErrors.ReviewItemNotFound, "There is no item with this id in this review for you to decide.");
        }

        var review = await context.AccessReviews.AsNoTracking().SingleAsync(r => r.Id == reviewId, cancellationToken).ConfigureAwait(false);
        if (!review.IsOpen)
        {
            return Error.Conflict(AuthorizationErrors.ReviewClosed, "This review is closed, so its items cannot be decided any more.");
        }

        bool removed;
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            await LockAsync(reviewId, cancellationToken).ConfigureAwait(false);
            if (await context.AccessReviewDecisions.AnyAsync(d => d.ItemId == itemId, cancellationToken).ConfigureAwait(false))
            {
                return Error.Conflict(AuthorizationErrors.ReviewItemDecided, "Someone has decided this item already. Reload the list to see the decision.");
            }

            removed = false;
            if (decision == ReviewDecision.Revoke)
            {
                var removal = await assignments.RemoveForReviewAsync(item.AssignmentId, reviewId, cancellationToken).ConfigureAwait(false);
                if (removal.IsFailure && removal.Error.Code != AuthorizationErrors.AssignmentNotFound)
                {
                    return removal.Error;
                }

                // An assignment removed since the snapshot is gone already: the revoke is recorded, with nothing left to remove.
                removed = removal.IsSuccess;
            }

            context.AccessReviewDecisions.Add(new AccessReviewDecision(reviewId, itemId, decision, comment, removed));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await WriteDecisionAsync(item, decision, comment, removed, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (removed)
        {
            grants.Forget(item.PrincipalId); // the removal path cleared it inside the transaction; a request between could have read the old row
        }

        return (await DescribeItemsAsync([item], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// Confirms the items of a page at once, behind the shell's confirmation: each must be the reader's to decide; any
    /// decided meanwhile is left as it was and counted.
    /// </summary>
    public async Task<Result<ConfirmManyView>> ConfirmManyAsync(Guid reviewId, IReadOnlyList<Guid> itemIds, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = itemIds.Distinct().ToArray();
        var items = await (await ItemsAsync(reviewId, ItemState.All, cancellationToken).ConfigureAwait(false)).Where(i => ids.Contains(i.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (items.Count != ids.Length)
        {
            return Error.NotFound(AuthorizationErrors.ReviewItemNotFound, "Some of these items are not in this review for you to decide. Nothing was confirmed; reload the list and try again.");
        }

        var review = await context.AccessReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reviewId, cancellationToken).ConfigureAwait(false);
        if (review is null)
        {
            return Error.NotFound(AuthorizationErrors.ReviewNotFound, "There is no access review with this id.");
        }

        if (!review.IsOpen)
        {
            return Error.Conflict(AuthorizationErrors.ReviewClosed, "This review is closed, so its items cannot be decided any more.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(reviewId, cancellationToken).ConfigureAwait(false);
        var decided = await context.AccessReviewDecisions.Where(d => ids.Contains(d.ItemId)).Select(d => d.ItemId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var open = items.Where(i => !decided.Contains(i.Id)).ToList();
        context.AccessReviewDecisions.AddRange(open.Select(i => new AccessReviewDecision(reviewId, i.Id, ReviewDecision.Confirm, comment, assignmentRemoved: false)));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in open)
        {
            await WriteDecisionAsync(item, ReviewDecision.Confirm, comment, removed: false, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConfirmManyView(open.Count, decided.Count);
    }

    /// <summary>Closes a campaign: no more decisions; what is open stays open in the record, and the audit event says how much.</summary>
    public async Task<Result<ReviewView>> CloseAsync(Guid id, CancellationToken cancellationToken)
    {
        var review = await context.AccessReviews.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (review is null)
        {
            return Error.NotFound(AuthorizationErrors.ReviewNotFound, "There is no access review with this id.");
        }

        if (!review.IsOpen)
        {
            return Error.Conflict(AuthorizationErrors.ReviewClosed, "This review is closed already.");
        }

        var progress = (await ProgressAsync([review], cancellationToken).ConfigureAwait(false))[review.Id];
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            await LockAsync(id, cancellationToken).ConfigureAwait(false);
            review.Close(time.GetUtcNow(), current.Id);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["decided"] = progress.Decided.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["open"] = progress.Open.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.ReviewClosed, AuditOutcome.Success, current.Id, review.Name, details, RecordType, review.Id), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await DescribeAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The decisions of one campaign one after the other, so an item is decided once and a closing waits for a decision
    /// under way. The key is the campaign's id folded to 64 bits; a clash only makes two campaigns wait for each other.
    /// </summary>
    internal static long LockKey(Guid reviewId)
    {
        var bytes = reviewId.ToByteArray();
        return BitConverter.ToInt64(bytes, 0) ^ BitConverter.ToInt64(bytes, 8);
    }

    private Task LockAsync(Guid reviewId, CancellationToken cancellationToken)
    {
        var key = LockKey(reviewId);
        return context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({key})", cancellationToken);
    }

    private async Task<bool> ManagesAsync(CancellationToken cancellationToken) =>
        (await grants.OfAsync(current.Id, cancellationToken).ConfigureAwait(false)).TenantWide.Contains(AuthorizationPermissions.ReviewsManage.Name);

    private async Task<IQueryable<AccessReviewItem>> DecidableAsync(CancellationToken cancellationToken)
    {
        var access = await grants.AccessibleAsync(current.Id, AuthorizationPermissions.ReviewsDecide, AccessReviewItem.RecordType, cancellationToken).ConfigureAwait(false);
        return context.AccessReviewItems.AsNoTracking().WhereAccessible(access, item => (Guid?)item.UnitId, item => item.Id);
    }

    /// <summary>Every unit the campaign covers, by id: the chosen units, and every unit below those chosen with their children.</summary>
    private async Task<Dictionary<Guid, OrganisationUnit>> CoveredAsync(IReadOnlyList<ReviewUnitInput> units, IReadOnlyList<OrganisationUnit> chosen, CancellationToken cancellationToken)
    {
        var exact = units.Where(u => !u.IncludeChildren).Select(u => u.UnitId).ToArray();
        var topIds = units.Where(u => u.IncludeChildren).Select(u => u.UnitId).ToArray();
        var tops = context.OrganisationUnits.Where(top => topIds.Contains(top.Id));
        var below = topIds.Length == 0
            ? []
            : await context.OrganisationUnits.AsNoTracking().Where(u => tops.Any(top => u.Path.StartsWith(top.Path))).ToListAsync(cancellationToken).ConfigureAwait(false);
        return chosen.Where(u => exact.Contains(u.Id)).Concat(below).DistinctBy(u => u.Id).ToDictionary(u => u.Id);
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> unitIds, CancellationToken cancellationToken)
    {
        var ids = unitIds.Distinct().ToArray();
        return await context.OrganisationUnits.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken).ConfigureAwait(false);
    }

    private Task WriteDecisionAsync(AccessReviewItem item, ReviewDecision decision, string? comment, bool removed, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["access_review_id"] = item.ReviewId.ToString("D"),
            ["decision"] = decision == ReviewDecision.Confirm ? "confirm" : "revoke",
            ["assignment_id"] = item.AssignmentId.ToString("D"),
            ["principal_id"] = item.PrincipalId.ToString("D"),
            ["role"] = item.RoleCode,
            ["unit_id"] = item.UnitId.ToString("D"),
            ["assignment_removed"] = removed ? "true" : "false",
        };
        if (!string.IsNullOrWhiteSpace(comment))
        {
            details["comment"] = comment.Trim()[..Math.Min(comment.Trim().Length, AccessReviewDecision.CommentMaxLength)];
        }

        return audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.ReviewDecided, AuditOutcome.Success, current.Id, item.PrincipalName, details, AccessReviewItem.RecordType, item.Id), cancellationToken);
    }
}
