using System.Text.Json;
using FluentValidation;
using Iron.Kernel.Api.Http;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Compliance.Data;
using Iron.Kernel.Compliance.Services;
using Iron.Kernel.Data.Lists;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Compliance.Api;

/// <summary>The credentials of a signature, as the signing dialog sends them.</summary>
public sealed record SigningCredentialsRequest(string? LoginId, string? Password, string? Code, string? RecoveryCode, JsonElement? Passkey)
{
    public SigningCredentials ToCredentials() => new(LoginId, Password, Code, RecoveryCode, Passkey);

    public static SigningCredentials Of(SigningCredentialsRequest? request) => request?.ToCredentials() ?? SigningCredentials.None;
}

public sealed class SigningCredentialsRequestValidator : AbstractValidator<SigningCredentialsRequest>
{
    public SigningCredentialsRequestValidator()
    {
        RuleFor(r => r.LoginId).MaximumLength(FieldLimits.LoginId);
        RuleFor(r => r.Password).MaximumLength(FieldLimits.Password);
        RuleFor(r => r.Code).MaximumLength(FieldLimits.OneTimeCode);
        RuleFor(r => r.RecoveryCode).MaximumLength(FieldLimits.RecoveryCode);
        RuleFor(r => r.Passkey).MaximumJsonSize(FieldLimits.PasskeyBytes);
    }
}

public sealed record SigningPolicyResponse(bool Required, IReadOnlyList<string> Meanings, bool Reauthenticate)
{
    public static SigningPolicyResponse From(SigningPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new(policy.Required, policy.Meanings, policy.Reauthenticate);
    }
}

/// <summary>A document type that can be signed, with the tenant's policy for it and its module's default.</summary>
public sealed record SignableTypeResponse(string RecordType, string Name, IReadOnlyList<string> Meanings, bool AlwaysSigned, SigningPolicyResponse Policy, SigningPolicyResponse Default);

public sealed record SignableTypesResponse(IReadOnlyList<SignableTypeResponse> Data);

/// <summary>What the signing dialog asks for now (ADR-0051), and the document type's policy.</summary>
public sealed record SignerChallengeResponse(
    bool FirstInSession,
    bool LoginId,
    bool Password,
    bool SecondFactor,
    IReadOnlyList<string> Methods,
    JsonElement? PasskeyOptions,
    Guid? ProviderId,
    string? ProviderName,
    bool ProviderConfirmed,
    bool Reauthenticate,
    SigningPolicyResponse Policy);

/// <summary>A signature (TF-063 AC2, AC3): who, how, when, what it means, its version, and its hash checked.</summary>
public sealed record SignatureResponse(
    Guid Id,
    string RecordType,
    Guid RecordId,
    int RecordVersion,
    Guid SignerId,
    string SignerName,
    string SignerLoginId,
    string Method,
    string Meaning,
    string? Comment,
    DateTimeOffset SignedAt,
    string SnapshotHash,
    bool HashMatches,
    int? CurrentVersion,
    bool RecordChanged)
{
    public static SignatureResponse From(SignatureView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new(view.Id, view.RecordType, view.RecordId, view.RecordVersion, view.SignerId, view.SignerName, view.SignerLoginId, view.Method, view.Meaning, view.Comment, view.SignedAt, view.SnapshotHash, view.HashMatches, view.CurrentVersion, view.RecordChanged);
    }
}

public sealed record SignaturesResponse(IReadOnlyList<SignatureResponse> Data);

/// <summary>
/// The snapshot a signature signed (TF-063 AC3): the canonical text as kept, the hash recomputed from it now beside the
/// hash kept, the record type's name and the content's fields.
/// </summary>
public sealed record SignedSnapshotResponse(SignatureResponse Signature, string TypeName, string Canonical, int Form, string RecomputedHash, JsonElement Content);

public sealed record AttachSignatureRequest(string RecordType, Guid RecordId);

public sealed class AttachSignatureRequestValidator : AbstractValidator<AttachSignatureRequest>
{
    public AttachSignatureRequestValidator()
    {
        RuleFor(r => r.RecordType).NotEmpty().MaximumLength(Signature.RecordTypeMaxLength);
    }
}

public sealed record ReviewSearchRequest(string Label, string Link);

public sealed record RecordAuditReviewRequest(
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    IReadOnlyList<Guid>? UnitIds,
    IReadOnlyList<string>? EventTypes,
    string Outcome,
    string? Notes,
    IReadOnlyList<ReviewSearchRequest>? Searches,
    SigningCredentialsRequest? Signature);

/// <summary>The limits the review's own checks apply (AuditTrailReviews), declared so a request over them is refused first.</summary>
public sealed class RecordAuditReviewRequestValidator : AbstractValidator<RecordAuditReviewRequest>
{
    /// <summary>The units or event types a review names; none means all.</summary>
    public const int ChoicesMaxCount = 200;

    public RecordAuditReviewRequestValidator()
    {
        RuleFor(r => r.UnitIds).MaximumCount(ChoicesMaxCount);
        RuleFor(r => r.EventTypes).MaximumCount(ChoicesMaxCount);
        RuleForEach(r => r.EventTypes).MaximumLength(100);
        RuleFor(r => r.Outcome).NotEmpty().MaximumLength(20);
        RuleFor(r => r.Notes).MaximumLength(AuditTrailReview.NotesMaxLength);
        RuleFor(r => r.Searches).MaximumCount(AuditTrailReviews.MaxSearches);
        RuleForEach(r => r.Searches).ChildRules(search =>
        {
            search.RuleFor(s => s.Label).MaximumLength(200);
            search.RuleFor(s => s.Link).MaximumLength(2000);
        });
        RuleFor(r => r.Signature!).SetValidator(new SigningCredentialsRequestValidator());
    }
}

public sealed record AuditReviewResponse(
    Guid Id,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    IReadOnlyList<Guid> UnitIds,
    IReadOnlyList<string> EventTypes,
    string Outcome,
    string? Notes,
    IReadOnlyList<ReviewSearch> Searches,
    Guid ReviewerId,
    string ReviewerName,
    DateTimeOffset RecordedAt,
    SignatureResponse? Signature)
{
    public static AuditReviewResponse From(AuditReviewView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var r = view.Review;
        return new(r.Id, r.PeriodFrom, r.PeriodTo, r.UnitIds, r.EventTypes, r.Outcome, r.Notes, view.Searches, r.ReviewerId, r.ReviewerName, r.CreatedAt, view.Signature is null ? null : SignatureResponse.From(view.Signature));
    }
}

public sealed record AuditReviewRowResponse(Guid Id, DateOnly PeriodFrom, DateOnly PeriodTo, string Outcome, Guid ReviewerId, string ReviewerName, DateTimeOffset RecordedAt);

public sealed record ReviewMonthResponse(DateOnly From, DateOnly To, IReadOnlyList<Guid> Reviews, IReadOnlyList<string> Outcomes);

public sealed record ReviewCoverageResponse(IReadOnlyList<ReviewMonthResponse> Months);

/// <summary>The period an evidence pack covers, as the person chose it: from, and up to (TF-067 AC1).</summary>
public sealed record RequestEvidencePackRequest(DateTimeOffset From, DateTimeOffset To);

/// <summary>
/// An evidence pack (TF-067): its period, who asked and when, where it stands, and once made its size, its hash and its
/// manifest's, and the audit chain's head it names with that position's anchor.
/// </summary>
public sealed record EvidencePackResponse(
    Guid Id,
    DateTimeOffset PeriodFrom,
    DateTimeOffset PeriodTo,
    Guid RequestedBy,
    string RequesterName,
    DateTimeOffset RequestedAt,
    string Status,
    long? Bytes,
    string? Sha256,
    string? ManifestSha256,
    int? ChainSegment,
    long? ChainPosition,
    string? ChainHash,
    Guid? AnchorId,
    DateTimeOffset? MadeAt,
    string? Failure)
{
    public static EvidencePackResponse From(EvidencePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return new(pack.Id, pack.PeriodFrom, pack.PeriodTo, pack.RequestedById, pack.RequesterName, pack.CreatedAt, pack.Status, pack.Bytes, pack.Sha256, pack.ManifestSha256, pack.ChainSegment, pack.ChainPosition, pack.ChainHash, pack.AnchorId, pack.MadeAt, pack.Failure);
    }
}

public sealed record EvidenceLinkResponse(string Url, DateTimeOffset ExpiresAt);

/// <summary>The version this tenant runs and whether its validation pack is here to download (TF-218 AC3).</summary>
public sealed record ValidationResponse(string Version, bool Development, bool PackAvailable, long? PackBytes);

/// <summary>
/// Signatures and audit-trail reviews (TF-063, TF-219, ADR-0051). Reading a record's signatures and their snapshots needs
/// what reading the record needs, which its module says; signing happens inside each module's own action. Reviews are
/// read with <c>compliance.audit_reviews.read</c> and recorded, always signed, with <c>compliance.audit_reviews.record</c>.
/// </summary>
internal static class ComplianceEndpoints
{
    public static readonly ListFields<AuditTrailReview> ReviewFields = new ListFields<AuditTrailReview>()
        .Add("id", r => r.Id, FieldUse.Select | FieldUse.Filter)
        .Add("period_from", r => r.PeriodFrom, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("period_to", r => r.PeriodTo, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("outcome", r => r.Outcome, FieldUse.Select | FieldUse.Filter)
        .Add("reviewer_id", r => r.ReviewerId, FieldUse.Select | FieldUse.Filter)
        .Add("recorded_at", r => r.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("period_from", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var signatures = endpoints.MapApiGroup("signatures");
        signatures.MapGet("/types", TypesAsync).RequireSignedIn().Produces<SignableTypesResponse>(StatusCodes.Status200OK).WithName("ListSignableTypes").WithSummary("The document types that can be signed, each with the tenant's signing policy and its default.");
        signatures.MapGet("/challenge", ChallengeAsync).RequireSignedIn().Produces<SignerChallengeResponse>(StatusCodes.Status200OK).WithName("GetSigningChallenge").WithSummary("What your next signature of a document type asks for: the login identifier at the first of a session, the password, the second factor, or a fresh company sign-in.");
        signatures.MapGet("/", ForRecordAsync).RequireSignedIn().Produces<SignaturesResponse>(StatusCodes.Status200OK).WithName("ListRecordSignatures").WithSummary("A record's signatures, oldest first, each with its hash checked against its snapshot and whether the record has changed since.");
        signatures.MapGet("/{id:guid}/snapshot", SnapshotAsync).RequireSignedIn().Produces<SignedSnapshotResponse>(StatusCodes.Status200OK).WithName("GetSignedSnapshot").WithSummary("The snapshot a signature signed, its canonical text and its hash recomputed now.");
        signatures.MapPost("/{id:guid}/attach", AttachAsync).RequireSignedIn().Validate<AttachSignatureRequest>().Produces<SignatureResponse>(StatusCodes.Status200OK).WithName("AttachSignature").WithSummary("Apply a signature to a record: refused, and recorded, unless the record's current snapshot has the signature's hash.");

        var reviews = endpoints.MapApiGroup("audit-reviews");
        reviews.MapGet("/", ListReviewsAsync).RequirePermission(CompliancePermissions.AuditReviewsRead).Produces<ListResponse<AuditReviewRowResponse>>(StatusCodes.Status200OK).WithName("ListAuditReviews").WithSummary("The reviews of the audit trail, newest period first.");
        reviews.MapGet("/coverage", CoverageAsync).RequirePermission(CompliancePermissions.AuditReviewsRead).Produces<ReviewCoverageResponse>(StatusCodes.Status200OK).WithName("GetAuditReviewCoverage").WithSummary("The last months (?months=, 12 by default), each with the reviews that cover it, so a month nobody reviewed shows.");
        reviews.MapGet("/{id:guid}", GetReviewAsync).RequirePermission(CompliancePermissions.AuditReviewsRead).Produces<AuditReviewResponse>(StatusCodes.Status200OK).WithName("GetAuditReview").WithSummary("One review of the audit trail with its signature.");
        reviews.MapPost("/", RecordReviewAsync).RequirePermission(CompliancePermissions.AuditReviewsRecord).Validate<RecordAuditReviewRequest>().Produces<AuditReviewResponse>(StatusCodes.Status201Created).WithName("RecordAuditReview").WithSummary("Record a review of the audit trail for a period and sign it with the meaning reviewed: your credentials as the signing rule asks.");

        var packs = endpoints.MapApiGroup("evidence-packs");
        packs.MapPost("/", RequestPackAsync).RequirePermission(CompliancePermissions.EvidenceExport).RateLimited(RateLimitPolicies.Exports).Produces<EvidencePackResponse>(StatusCodes.Status202Accepted).WithName("RequestEvidencePack").WithSummary("Ask for an evidence pack for a period; the unit makes it within seconds. The request is audited with you as its requester.");
        packs.MapGet("/", ListPacksAsync).RequirePermission(CompliancePermissions.EvidenceExport).Produces<ListResponse<EvidencePackResponse>>(StatusCodes.Status200OK).WithName("ListEvidencePacks").WithSummary("The evidence packs made, newest first.");
        packs.MapGet("/{id:guid}", GetPackAsync).RequirePermission(CompliancePermissions.EvidenceExport).Produces<EvidencePackResponse>(StatusCodes.Status200OK).WithName("GetEvidencePack").WithSummary("One evidence pack and where it stands.");
        packs.MapPost("/{id:guid}/links", LinkAsync).RequirePermission(CompliancePermissions.EvidenceExport).Produces<EvidenceLinkResponse>(StatusCodes.Status201Created).WithName("LinkEvidencePack").WithSummary("A link to download a ready pack: for you, once, within five minutes.");
        packs.MapGet("/downloads/{token}", DownloadPackAsync).RequirePermission(CompliancePermissions.EvidenceExport).Produces(StatusCodes.Status200OK, typeof(byte[]), "application/zip").WithName("DownloadEvidencePack").WithSummary("Download a pack through its link, its bytes checked against its hash; the download is audited.");

        var validation = endpoints.MapApiGroup("validation");
        validation.MapGet("/", ValidationAsync).RequirePermission(CompliancePermissions.ValidationRead).Produces<ValidationResponse>(StatusCodes.Status200OK).WithName("GetValidation").WithSummary("The version this tenant runs, and whether its validation pack is here.");
        validation.MapGet("/pack", ValidationPackAsync).RequirePermission(CompliancePermissions.ValidationRead).RateLimited(RateLimitPolicies.Exports).Produces(StatusCodes.Status200OK, typeof(byte[]), "application/zip").WithName("DownloadValidationPack").WithSummary("Download the validation pack of the version this tenant runs; the download is audited.");
    }

    private static async Task<IResult> RequestPackAsync(RequestEvidencePackRequest request, HttpContext http, EvidencePacks packs, CancellationToken cancellationToken)
    {
        var requested = await packs.RequestAsync(request.From, request.To, cancellationToken);
        return ApiResults.From(requested, http, pack => TypedResults.Accepted($"{ApiRouting.Prefix}/evidence-packs/{pack.Id}", EvidencePackResponse.From(pack)));
    }

    private static async Task<IResult> ListPacksAsync(HttpContext http, EvidencePacks packs, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "page_number")] int? pageNumber, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "page_size")] int? pageSize, CancellationToken cancellationToken)
    {
        var page = await packs.ListAsync(PageRequest.From(pageNumber, pageSize), cancellationToken);
        return ApiResults.List(page.Map(EvidencePackResponse.From), http);
    }

    private static async Task<IResult> GetPackAsync(Guid id, HttpContext http, EvidencePacks packs, CancellationToken cancellationToken)
    {
        var pack = await packs.FindAsync(id, cancellationToken);
        return pack is null
            ? ApiResults.Problem(Error.NotFound(ComplianceErrors.EvidencePackNotFound, "There is no such evidence pack."), http)
            : TypedResults.Ok(EvidencePackResponse.From(pack));
    }

    private static async Task<IResult> LinkAsync(Guid id, HttpContext http, EvidencePacks packs, CancellationToken cancellationToken)
    {
        var link = await packs.LinkAsync(id, cancellationToken);
        return ApiResults.From(link, http, made => TypedResults.Created(made.Url, new EvidenceLinkResponse(made.Url, made.ExpiresAt)));
    }

    private static async Task<IResult> DownloadPackAsync(string token, HttpContext http, EvidencePacks packs, CancellationToken cancellationToken)
    {
        var opened = await packs.OpenAsync(token, cancellationToken);
        if (opened.IsFailure)
        {
            return ApiResults.Problem(opened.Error, http);
        }

        var pack = opened.Value.Pack;
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.File(opened.Value.Content, "application/zip", $"evidence-pack-{pack.PeriodFrom.UtcDateTime:yyyyMMdd}-{pack.PeriodTo.UtcDateTime:yyyyMMdd}.zip");
    }

    private static async Task<IResult> ValidationAsync(ValidationPacks validation, CancellationToken cancellationToken)
    {
        var installed = await validation.InstalledAsync(cancellationToken);
        return TypedResults.Ok(new ValidationResponse(installed.Version, installed.Development, installed.Available, installed.Bytes));
    }

    private static async Task<IResult> ValidationPackAsync(HttpContext http, ValidationPacks validation, CancellationToken cancellationToken)
    {
        var opened = await validation.OpenAsync(cancellationToken);
        if (opened.IsFailure)
        {
            return ApiResults.Problem(opened.Error, http);
        }

        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.File(opened.Value.Content, "application/zip", opened.Value.FileName);
    }

    private static async Task<IResult> TypesAsync(SignableCatalogue catalogue, SigningPolicies policies, CancellationToken cancellationToken)
    {
        var types = new List<SignableTypeResponse>();
        foreach (var type in catalogue.Types)
        {
            var policy = await policies.ForAsync(type.RecordType, cancellationToken);
            types.Add(new SignableTypeResponse(type.RecordType, type.Name, type.Meanings, type.AlwaysSigned, SigningPolicyResponse.From(policy), SigningPolicyResponse.From(type.Default)));
        }

        return TypedResults.Ok(new SignableTypesResponse(types));
    }

    private static async Task<IResult> ChallengeAsync(HttpContext http, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "record_type")] string? recordType, ISignatures signatures, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recordType))
        {
            return ApiResults.Problem(Error.Validation(ComplianceErrors.TypeUnknown, "Say which document type you are signing (record_type)."), http);
        }

        var policy = await signatures.PolicyAsync(recordType, cancellationToken);
        var challenge = await signatures.ChallengeAsync(recordType, cancellationToken);
        return ApiResults.From(challenge, http, c => TypedResults.Ok(new SignerChallengeResponse(
            c.FirstInSession, c.LoginId, c.Password, c.SecondFactor, c.Methods, c.PasskeyOptions is { } json ? JsonDocument.Parse(json).RootElement.Clone() : null, c.ProviderId, c.ProviderName, c.ProviderConfirmed, c.Reauthenticate, SigningPolicyResponse.From(policy))));
    }

    private static async Task<IResult> ForRecordAsync(HttpContext http, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "record_type")] string? recordType, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "record_id")] Guid? recordId, SignableCatalogue catalogue, ISignatures signatures, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recordType) || recordId is not { } id)
        {
            return ApiResults.Problem(Error.Validation(ComplianceErrors.TypeUnknown, "Say whose signatures you want: record_type and record_id."), http);
        }

        if (await RefusalAsync(http, catalogue, recordType, id, cancellationToken) is { } refused)
        {
            return refused;
        }

        var found = await signatures.ForRecordAsync(recordType, id, cancellationToken);
        return TypedResults.Ok(new SignaturesResponse([.. found.Select(SignatureResponse.From)]));
    }

    private static async Task<IResult> SnapshotAsync(Guid id, HttpContext http, SignatureService service, SignableCatalogue catalogue, CancellationToken cancellationToken)
    {
        var found = await service.FindAsync(id, cancellationToken);
        if (found is not { } pair)
        {
            return ApiResults.Problem(Error.NotFound(ComplianceErrors.NotFound, "There is no signature with this id."), http);
        }

        if (await RefusalAsync(http, catalogue, pair.Signature.RecordType, pair.Signature.RecordId, cancellationToken) is { } refused)
        {
            return refused;
        }

        using var document = JsonDocument.Parse(pair.Signature.Snapshot);
        var content = document.RootElement[4].Clone();
        var name = catalogue.Find(pair.Signature.RecordType)?.Name ?? pair.Signature.RecordType;
        return TypedResults.Ok(new SignedSnapshotResponse(SignatureResponse.From(pair.View), name, pair.Signature.Snapshot, pair.Signature.SnapshotForm, SnapshotCanonicalForm.Hash(pair.Signature.Snapshot), content));
    }

    private static async Task<IResult> AttachAsync(Guid id, AttachSignatureRequest request, HttpContext http, SignatureService service, SignableCatalogue catalogue, CancellationToken cancellationToken)
    {
        if (await RefusalAsync(http, catalogue, request.RecordType, request.RecordId, cancellationToken) is { } refused)
        {
            return refused;
        }

        var attached = await service.AttachAsync(id, request.RecordType, request.RecordId, cancellationToken);
        return ApiResults.From(attached, http, view => TypedResults.Ok(SignatureResponse.From(view)));
    }

    private static async Task<IResult> ListReviewsAsync(HttpContext http, ComplianceDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, ReviewFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.AuditTrailReviews.AsNoTracking().ApplyListQuery(query, ReviewFields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(r => new AuditReviewRowResponse(r.Id, r.PeriodFrom, r.PeriodTo, r.Outcome, r.ReviewerId, r.ReviewerName, r.CreatedAt)), http, query);
    }

    private static async Task<IResult> CoverageAsync(int? months, AuditTrailReviews reviews, CancellationToken cancellationToken)
    {
        var found = await reviews.CoverageAsync(months ?? 12, cancellationToken);
        return TypedResults.Ok(new ReviewCoverageResponse([.. found.Select(m => new ReviewMonthResponse(m.From, m.To, m.Reviews, m.Outcomes))]));
    }

    private static async Task<IResult> GetReviewAsync(Guid id, HttpContext http, AuditTrailReviews reviews, CancellationToken cancellationToken)
    {
        var found = await reviews.FindAsync(id, cancellationToken);
        return found is null
            ? ApiResults.Problem(Error.NotFound(ComplianceErrors.ReviewNotFound, "There is no review of the audit trail with this id."), http)
            : TypedResults.Ok(AuditReviewResponse.From(found));
    }

    private static async Task<IResult> RecordReviewAsync(RecordAuditReviewRequest request, HttpContext http, AuditTrailReviews reviews, CancellationToken cancellationToken)
    {
        var input = new AuditReviewInput(
            request.PeriodFrom,
            request.PeriodTo,
            request.UnitIds ?? [],
            request.EventTypes ?? [],
            request.Outcome ?? string.Empty,
            request.Notes,
            [.. (request.Searches ?? []).Select(s => new ReviewSearch(s.Label ?? string.Empty, s.Link ?? string.Empty))]);
        var recorded = await reviews.RecordAsync(input, SigningCredentialsRequest.Of(request.Signature), cancellationToken);
        return ApiResults.From(recorded, http, view => TypedResults.Created($"/api/v1/audit-reviews/{view.Review.Id}", AuditReviewResponse.From(view)));
    }

    /// <summary>A record's signatures are read by those who may read the record, as its module says.</summary>
    private static async Task<IResult?> RefusalAsync(HttpContext http, SignableCatalogue catalogue, string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        if (catalogue.SourceOf(recordType) is not { } source)
        {
            return ApiResults.Problem(Error.NotFound(ComplianceErrors.TypeUnknown, "Records of this type are not signed here."), http);
        }

        return await source.MayReadAsync(recordType, recordId, cancellationToken)
            ? null
            : ApiResults.Problem(Error.Forbidden(PermissionErrors.PermissionDenied, "You may not read this record, so you may not see its signatures."), http);
    }
}
