using System.Globalization;
using System.Text.Json;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Compliance.Data;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Compliance.Services;

/// <summary>A search the reviewer ran: its label and its link into the audit trail, a path in the shell.</summary>
public sealed record ReviewSearch(string Label, string Link);

/// <summary>What a review records (TF-219 AC3): the period, the scope, the outcome with its notes, and the searches.</summary>
public sealed record AuditReviewInput(DateOnly PeriodFrom, DateOnly PeriodTo, IReadOnlyList<Guid> UnitIds, IReadOnlyList<string> EventTypes, string Outcome, string? Notes, IReadOnlyList<ReviewSearch> Searches);

/// <summary>A review with its signature.</summary>
public sealed record AuditReviewView(AuditTrailReview Review, IReadOnlyList<ReviewSearch> Searches, SignatureView? Signature);

/// <summary>One month of the period the console shows (TF-219 AC3): the reviews that cover any of it, or none, so a missing month shows.</summary>
public sealed record ReviewMonth(DateOnly From, DateOnly To, IReadOnlyList<Guid> Reviews, IReadOnlyList<string> Outcomes);

/// <summary>
/// Reviews of the audit trail (TF-219 AC3, ADR-0051): a permitted person records the period, the scope, the outcome and the
/// searches they ran, and signs it with the meaning "reviewed". Always signed, whatever the tenant's policy. The reviewer
/// is the signer, proved before the review is written, so the review and its signature commit together.
/// </summary>
public sealed class AuditTrailReviews(ComplianceDbContext context, ISignatures signatures, IUnitOfWork unitOfWork, IAuditWriter audit, TimeProvider time)
{
    public const int MaxSearches = 20;
    public const int MaxMonths = 36;

    private static readonly JsonSerializerOptions SearchJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static SignableType Type { get; } = new(
        AuditTrailReview.RecordType,
        "Audit-trail review",
        [SignatureMeanings.Reviewed],
        new SigningPolicy(true, [SignatureMeanings.Reviewed], true),
        AlwaysSigned: true);

    public async Task<Result<AuditReviewView>> RecordAsync(AuditReviewInput input, SigningCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(credentials);
        if (Invalid(input) is { } invalid)
        {
            return invalid;
        }

        // The signer first, outside the review's transaction: a wrong password counts whatever happens next.
        var proof = await signatures.AuthenticateAsync(AuditTrailReview.RecordType, credentials, cancellationToken).ConfigureAwait(false);
        if (proof.IsFailure)
        {
            return proof.Error;
        }

        var searches = JsonSerializer.Serialize(input.Searches.Select(s => new ReviewSearch(s.Label.Trim(), s.Link.Trim())).ToList(), SearchJson);
        var review = new AuditTrailReview(input.PeriodFrom, input.PeriodTo, input.UnitIds, input.EventTypes, input.Outcome, input.Notes, searches, proof.Value.Signer.SignerId, proof.Value.Signer.FullName);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.AuditTrailReviews.Add(review);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var signed = await signatures.RecordAsync(proof.Value, Snapshot(review), SignatureMeanings.Reviewed, null, cancellationToken).ConfigureAwait(false);
        if (signed.IsFailure)
        {
            return signed.Error;
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["period_from"] = review.PeriodFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["period_to"] = review.PeriodTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["outcome"] = review.Outcome,
            ["units"] = review.UnitIds.Count == 0 ? "all" : string.Join(' ', review.UnitIds),
            ["event_types"] = review.EventTypes.Count == 0 ? "all" : string.Join(' ', review.EventTypes),
            ["signature_id"] = signed.Value.Id.ToString("D"),
        };
        await audit.WriteAsync(new AuditEvent(ComplianceAuditEvents.AuditReviewRecorded, AuditOutcome.Success, review.ReviewerId, Title(review), details, AuditTrailReview.RecordType, review.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AuditReviewView(review, SearchesOf(review), signed.Value);
    }

    public async Task<AuditReviewView?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var review = await context.AuditTrailReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (review is null)
        {
            return null;
        }

        var signed = await signatures.ForRecordAsync(AuditTrailReview.RecordType, id, cancellationToken).ConfigureAwait(false);
        return new AuditReviewView(review, SearchesOf(review), signed.Count > 0 ? signed[0] : null);
    }

    /// <summary>The last <paramref name="months"/> calendar months, newest first, each with the reviews whose period touches it.</summary>
    public async Task<IReadOnlyList<ReviewMonth>> CoverageAsync(int months, CancellationToken cancellationToken)
    {
        months = Math.Clamp(months, 1, MaxMonths);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));
        var reviews = await context.AuditTrailReviews.AsNoTracking()
            .Where(r => r.PeriodTo >= first)
            .Select(r => new { r.Id, r.PeriodFrom, r.PeriodTo, r.Outcome })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new List<ReviewMonth>();
        for (var start = new DateOnly(today.Year, today.Month, 1); start >= first; start = start.AddMonths(-1))
        {
            var end = start.AddMonths(1).AddDays(-1);
            var touching = reviews.Where(r => r.PeriodFrom <= end && r.PeriodTo >= start).OrderBy(r => r.PeriodFrom).ToList();
            result.Add(new ReviewMonth(start, end, [.. touching.Select(r => r.Id)], [.. touching.Select(r => r.Outcome)]));
        }

        return result;
    }

    /// <summary>The review as it is signed: every field it records, in the snapshot form (ADR-0051).</summary>
    public static RecordSnapshot Snapshot(AuditTrailReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return RecordSnapshot.Of(AuditTrailReview.RecordType, review.Id, 1, Title(review), new
        {
            PeriodFrom = review.PeriodFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            PeriodTo = review.PeriodTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            UnitIds = review.UnitIds,
            EventTypes = review.EventTypes,
            review.Outcome,
            review.Notes,
            Searches = SearchesOf(review),
            ReviewerId = review.ReviewerId,
            ReviewerName = review.ReviewerName,
        });
    }

    public static string Title(AuditTrailReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return $"Audit-trail review {review.PeriodFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} to {review.PeriodTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }

    public static IReadOnlyList<ReviewSearch> SearchesOf(AuditTrailReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return JsonSerializer.Deserialize<List<ReviewSearch>>(review.SearchesJson, SearchJson) ?? [];
    }

    private Error? Invalid(AuditReviewInput input)
    {
        var fields = new List<FieldError>();
        if (input.PeriodTo < input.PeriodFrom)
        {
            fields.Add(new FieldError("period_to", "before_start", "The period ends before it starts. Choose an end on or after the start."));
        }

        if (input.PeriodTo > DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime))
        {
            fields.Add(new FieldError("period_to", "future", "A review covers time that has passed. Choose an end no later than today."));
        }

        if (!AuditReviewOutcomes.All.Contains(input.Outcome, StringComparer.Ordinal))
        {
            fields.Add(new FieldError("outcome", "unknown", "Choose no findings, or findings with notes."));
        }

        if (input.Outcome == AuditReviewOutcomes.Findings && string.IsNullOrWhiteSpace(input.Notes))
        {
            fields.Add(new FieldError("notes", "required", "Say what you found: findings need notes."));
        }

        if (input.Notes is { Length: > AuditTrailReview.NotesMaxLength })
        {
            fields.Add(new FieldError("notes", "too_long", $"Notes are at most {AuditTrailReview.NotesMaxLength} characters."));
        }

        if (input.Searches.Count > MaxSearches)
        {
            fields.Add(new FieldError("searches", "too_many", $"Link at most {MaxSearches} searches."));
        }

        // A link is a path into this shell's audit trail, never another site or a script.
        if (input.Searches.Any(s => string.IsNullOrWhiteSpace(s.Label) || s.Label.Length > 200 || !(s.Link == "/audit" || s.Link.StartsWith("/audit?", StringComparison.Ordinal)) || s.Link.Length > 2000))
        {
            fields.Add(new FieldError("searches", "invalid", "Each search needs a label and a link to the audit trail's search, as the page copies it."));
        }

        if (input.EventTypes.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 100))
        {
            fields.Add(new FieldError("event_types", "invalid", "An event type is a code from the audit trail's list."));
        }

        return fields.Count == 0 ? null : Error.Validation(ComplianceErrors.ReviewInvalid, "The review is not complete. Check the fields marked.", fields);
    }
}

/// <summary>Reviews as signable records: the snapshot of a review, and who may read one (<c>compliance.audit_reviews.read</c>).</summary>
public sealed class AuditTrailReviewRecords(ComplianceDbContext context, IPermissionEvaluator evaluator, ICurrentPrincipal actor) : ISignableRecords
{
    public IReadOnlyList<SignableType> Types => [AuditTrailReviews.Type];

    public async Task<RecordSnapshot?> CurrentAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        if (!string.Equals(recordType, AuditTrailReview.RecordType, StringComparison.Ordinal))
        {
            return null;
        }

        var review = await context.AuditTrailReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == recordId, cancellationToken).ConfigureAwait(false);
        return review is null ? null : AuditTrailReviews.Snapshot(review);
    }

    public async Task<bool> MayReadAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        var explained = await evaluator.ExplainAsync(new AccessRequest(actor.Id, CompliancePermissions.AuditReviewsRead, "read", "/api/v1/audit-reviews"), cancellationToken).ConfigureAwait(false);
        return explained.Allowed;
    }
}
