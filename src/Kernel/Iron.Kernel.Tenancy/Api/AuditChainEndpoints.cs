using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Tenancy.Api;

/// <summary>An anchor as a unit sends it: how far its chain reached, with which hash, and when it sealed or pruned.</summary>
public sealed record AuditAnchorRequest(string Kind, long Position, string Hash, DateTimeOffset At);

public sealed class AuditAnchorRequestValidator : AbstractValidator<AuditAnchorRequest>
{
    public AuditAnchorRequestValidator()
    {
        RuleFor(r => r.Kind).NotEmpty().MaximumLength(AuditAnchorRecord.KindMaxLength);
        RuleFor(r => r.Position).GreaterThanOrEqualTo(1);
        RuleFor(r => r.Hash).NotEmpty().Length(AuditAnchorRecord.HashLength);
    }
}

public sealed record AuditAnchorResponse(string Kind, long Position, string Hash, DateTimeOffset At, DateTimeOffset ReceivedAt, int Segment = 1, Guid? Id = null)
{
    public static AuditAnchorResponse From(AuditAnchorInfo anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return new(anchor.Kind, anchor.Position, anchor.Hash, anchor.At, anchor.ReceivedAt, anchor.Segment, anchor.Id);
    }
}

/// <summary>A restore an operator accepted: the segment it began, the restore point, the range lost, and who, when and why.</summary>
public sealed record AuditRestoreResponse(
    Guid Id,
    int Segment,
    long StartPosition,
    string PreviousHash,
    long RestorePosition,
    DateTimeOffset RestoreAt,
    long LostFrom,
    long LostTo,
    string Ticket,
    string Reason,
    string AcceptedBy,
    DateTimeOffset AcceptedAt)
{
    public static AuditRestoreResponse From(AuditRestoreInfo restore)
    {
        ArgumentNullException.ThrowIfNull(restore);
        return new(restore.Id, restore.Segment, restore.StartPosition, restore.PreviousHash, restore.RestorePosition, restore.RestoreAt, restore.LostFrom, restore.LostTo, restore.Ticket, restore.Reason, restore.AcceptedBy, restore.AcceptedAt);
    }
}

public sealed record AuditRestoresResponse(IReadOnlyList<AuditRestoreResponse> Restores);

/// <summary>An operator's acceptance of a restore point: a ticket and a reason, both kept with the record and in both trails.</summary>
public sealed record AuditRestoreAcceptance(string? Ticket, string? Reason);

public sealed class AuditRestoreAcceptanceValidator : AbstractValidator<AuditRestoreAcceptance>
{
    public AuditRestoreAcceptanceValidator()
    {
        RuleFor(r => r.Ticket).MaximumLength(AuditRestoreRecord.TicketMaxLength);
        RuleFor(r => r.Reason).MaximumLength(AuditRestoreRecord.ReasonMaxLength);
    }
}

public sealed record AuditRestoreAcceptedResponse(AuditRestoreResponse Restore, Guid VerificationRequestId);

/// <summary>Where a unit resumes: the latest anchors of its current segment, the segment, and every accepted restore.</summary>
public sealed record LatestAnchorsResponse(AuditAnchorResponse? Seal, AuditAnchorResponse? Pruned, int Segment = 1, IReadOnlyList<AuditRestoreResponse>? Restores = null);

public sealed record AuditChainProblemResponse(string Kind, long Position, Guid? EventId, string Message);

public sealed record AuditVerificationResponse(
    string TenantSlug,
    bool Full,
    long FromPosition,
    long ToPosition,
    string Result,
    int EventsChecked,
    IReadOnlyList<AuditChainProblemResponse> Problems,
    long? RestorePosition,
    DateTimeOffset? RestoreAt,
    DateTimeOffset VerifiedAt,
    Guid? Id = null)
{
    public static AuditVerificationResponse From(AuditVerificationInfo verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        return new(
            verification.TenantSlug,
            verification.Full,
            verification.FromPosition,
            verification.ToPosition,
            verification.Result,
            verification.EventsChecked,
            [.. verification.Problems.Select(p => new AuditChainProblemResponse(p.Kind, p.Position, p.EventId, p.Message))],
            verification.RestorePosition,
            verification.RestoreAt,
            verification.At,
            verification.Id);
    }
}

public sealed record LatestVerificationResponse(AuditVerificationResponse? Verification);

/// <summary>A unit's chain verifications in a period, oldest first, as its evidence pack lists them (ADR-0052).</summary>
public sealed record AuditVerificationsResponse(IReadOnlyList<AuditVerificationResponse> Verifications);

public sealed record AuditVerificationOrder(bool Full = false);

public sealed record AuditVerificationRequestResponse(Guid Id, string TenantSlug, bool Full, DateTimeOffset RequestedAt, AuditVerificationResponse? Verification);

/// <summary>
/// The tenants' audit chains (TF-061, ADR-0050). A unit's own client sends its anchors and reads its latest anchors and
/// verification; operators list them and ask for a verification now, which the fleet runs within a minute.
/// </summary>
internal static class AuditChainEndpoints
{
    public static readonly ListFields<AuditAnchorRecord> AnchorFields = new ListFields<AuditAnchorRecord>()
        .Add("kind", a => a.Kind, FieldUse.Select | FieldUse.Filter)
        .Add("segment", a => a.Segment, FieldUse.Select | FieldUse.Sort | FieldUse.Filter)
        .Add("position", a => a.Position, FieldUse.Select | FieldUse.Sort | FieldUse.Filter)
        .Add("hash", a => a.Hash, FieldUse.Select)
        .Add("at", a => a.At, FieldUse.Select | FieldUse.Sort | FieldUse.Filter)
        .Add("received_at", a => a.CreatedAt, FieldUse.Select | FieldUse.Sort | FieldUse.Filter)
        .SortByDefault("position", SortDirection.Descending);

    public static readonly ListFields<AuditVerificationRecord> VerificationFields = new ListFields<AuditVerificationRecord>()
        .Add("result", v => v.Result, FieldUse.Select | FieldUse.Filter)
        .Add("full", v => v.Full, FieldUse.Select | FieldUse.Filter)
        .Add("from_position", v => v.FromPosition, FieldUse.Select)
        .Add("to_position", v => v.ToPosition, FieldUse.Select)
        .Add("verified_at", v => v.CreatedAt, FieldUse.Select | FieldUse.Sort | FieldUse.Filter)
        .SortByDefault("verified_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var anchors = endpoints.MapApiGroup("audit-anchors");
        anchors.MapPost("/", AppendAsync).RequirePermission(TenancyPermissions.AuditAnchorsAppend).Validate<AuditAnchorRequest>().Produces<AuditAnchorResponse>(StatusCodes.Status201Created).Produces<AuditAnchorResponse>(StatusCodes.Status200OK).WithName("AppendAuditAnchor").WithSummary("A unit sends an anchor of its audit chain; the same anchor again answers it, another hash for the position is refused.");
        anchors.MapGet("/latest", LatestAnchorsAsync).RequirePermission(TenancyPermissions.AuditAnchorsAppend).Produces<LatestAnchorsResponse>(StatusCodes.Status200OK).WithName("LatestAuditAnchors").WithSummary("A unit's latest seal and pruned anchors: where it resumes sending.");

        var verifications = endpoints.MapApiGroup("audit-verifications");
        verifications.MapGet("/latest", LatestVerificationAsync).RequirePermission(TenancyPermissions.AuditAnchorsAppend).Produces<LatestVerificationResponse>(StatusCodes.Status200OK).WithName("LatestAuditVerification").WithSummary("A unit's latest chain verification, for its chain page.");
        verifications.MapGet("/", VerificationsOfCallerAsync).RequirePermission(TenancyPermissions.AuditAnchorsAppend).Produces<AuditVerificationsResponse>(StatusCodes.Status200OK).WithName("UnitAuditVerifications").WithSummary("A unit's own chain verifications made from one time up to another (?from=&to=), oldest first, for its evidence pack.");

        var tenants = endpoints.MapApiGroup("tenants");
        tenants.MapGet("/{slug}/audit-anchors", ListAnchorsAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<ListResponse<AuditAnchorResponse>>(StatusCodes.Status200OK).WithName("ListAuditAnchors").WithSummary("A tenant's audit anchors, newest first.");
        tenants.MapGet("/{slug}/audit-verifications", ListVerificationsAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<ListResponse<AuditVerificationResponse>>(StatusCodes.Status200OK).WithName("ListAuditVerifications").WithSummary("A tenant's chain verifications, newest first.");
        tenants.MapPost("/{slug}/audit-verifications", RequestAsync).RequirePermission(TenancyPermissions.AuditChainsVerify).Produces<AuditVerificationRequestResponse>(StatusCodes.Status202Accepted).WithName("RequestAuditVerification").WithSummary("Ask the fleet to verify a tenant's audit chain now, or in full; it runs within a minute.");
        tenants.MapPost("/{slug}/audit-restores", AcceptRestoreAsync).RequirePermission(TenancyPermissions.AuditRestoresAccept).RequireRecentAuthentication().Validate<AuditRestoreAcceptance>().Produces<AuditRestoreAcceptedResponse>(StatusCodes.Status201Created).WithName("AcceptAuditRestore").WithSummary("Accept the restore point a tenant's latest verification names, with a ticket and a reason and a fresh token: the chain continues as a new segment from it.");
        tenants.MapGet("/{slug}/audit-restores", ListRestoresAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<AuditRestoresResponse>(StatusCodes.Status200OK).WithName("ListAuditRestores").WithSummary("A tenant's accepted restores, oldest first: each segment, its restore point and the range lost.");
        tenants.MapGet("/{slug}/audit-verifications/requests/{id:guid}", RequestStatusAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<AuditVerificationRequestResponse>(StatusCodes.Status200OK).WithName("GetAuditVerificationRequest").WithSummary("A verification request, with its record once the fleet answered it.");
    }

    private static async Task<IResult> AppendAsync(AuditAnchorRequest request, HttpContext http, AuditChainService chains, CancellationToken cancellationToken)
    {
        var result = await chains.AppendAsync(request.Kind, request.Position, request.Hash, request.At, cancellationToken);
        return ApiResults.From(result, http, appended => appended.Created
            ? TypedResults.Created($"{ApiRouting.Prefix}/audit-anchors/latest", AuditAnchorResponse.From(appended.Anchor))
            : (IResult)TypedResults.Ok(AuditAnchorResponse.From(appended.Anchor)));
    }

    private static async Task<IResult> LatestAnchorsAsync(HttpContext http, AuditChainService chains, CancellationToken cancellationToken)
    {
        var result = await chains.LatestOfCallerAsync(cancellationToken);
        return ApiResults.From(result, http, latest => TypedResults.Ok(new LatestAnchorsResponse(
            latest.Seal is null ? null : AuditAnchorResponse.From(latest.Seal),
            latest.Pruned is null ? null : AuditAnchorResponse.From(latest.Pruned),
            latest.Segment,
            [.. latest.Restores.Select(AuditRestoreResponse.From)])));
    }

    private static async Task<IResult> AcceptRestoreAsync(string slug, AuditRestoreAcceptance? acceptance, HttpContext http, AuditChainService chains, CancellationToken cancellationToken)
    {
        var result = await chains.AcceptRestoreAsync(slug, acceptance?.Ticket, acceptance?.Reason, cancellationToken);
        return ApiResults.From(result, http, accepted => TypedResults.Created(
            $"{ApiRouting.Prefix}/tenants/{slug}/audit-restores",
            new AuditRestoreAcceptedResponse(AuditRestoreResponse.From(accepted.Restore), accepted.VerificationRequestId)));
    }

    private static async Task<IResult> ListRestoresAsync(string slug, AuditChainService chains, CancellationToken cancellationToken)
    {
        var restores = await chains.RestoresAsync(slug, cancellationToken);
        return TypedResults.Ok(new AuditRestoresResponse([.. restores.Select(AuditRestoreResponse.From)]));
    }

    private static async Task<IResult> LatestVerificationAsync(HttpContext http, AuditChainService chains, CancellationToken cancellationToken)
    {
        var result = await chains.LatestVerificationOfCallerAsync(cancellationToken);
        return ApiResults.From(result, http, latest => TypedResults.Ok(new LatestVerificationResponse(latest is null ? null : AuditVerificationResponse.From(latest))));
    }

    private static async Task<IResult> VerificationsOfCallerAsync(HttpContext http, AuditChainService chains, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        if (from is null || to is null || to < from)
        {
            return ApiResults.Problem(Error.Validation("tenancy.audit_verifications.range_required", "Give the period: from and to, to not before from."), http);
        }

        var result = await chains.VerificationsOfCallerAsync(from.Value, to.Value, cancellationToken);
        return ApiResults.From(result, http, found => TypedResults.Ok(new AuditVerificationsResponse([.. found.Select(AuditVerificationResponse.From)])));
    }

    private static async Task<IResult> ListAnchorsAsync(string slug, HttpContext http, TenancyDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, AnchorFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.AuditAnchors.AsNoTracking().Where(a => a.TenantSlug == slug).ApplyListQuery(query, AnchorFields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(a => new AuditAnchorResponse(a.Kind, a.Position, a.Hash, a.At, a.CreatedAt, a.Segment)), http, query);
    }

    private static async Task<IResult> ListVerificationsAsync(string slug, HttpContext http, TenancyDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, VerificationFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.AuditVerifications.AsNoTracking().Where(v => v.TenantSlug == slug).ApplyListQuery(query, VerificationFields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(v => AuditVerificationResponse.From(AuditChainService.Info(v))), http, query);
    }

    private static async Task<IResult> RequestAsync(string slug, AuditVerificationOrder? order, HttpContext http, AuditChainService chains, CancellationToken cancellationToken)
    {
        var result = await chains.RequestAsync(slug, order?.Full ?? false, cancellationToken);
        return ApiResults.From(result, http, request => TypedResults.Accepted(
            $"{ApiRouting.Prefix}/tenants/{slug}/audit-verifications/requests/{request.Id}",
            new AuditVerificationRequestResponse(request.Id, request.TenantSlug, request.Full, request.CreatedAt, null)));
    }

    private static async Task<IResult> RequestStatusAsync(string slug, Guid id, HttpContext http, AuditChainService chains, CancellationToken cancellationToken)
    {
        var found = await chains.FindRequestAsync(slug, id, cancellationToken);
        if (found is not { } pair)
        {
            return ApiResults.Problem(Error.NotFound("tenancy.audit_verification.not_found", $"Tenant '{slug}' has no verification request {id}."), http);
        }

        return TypedResults.Ok(new AuditVerificationRequestResponse(pair.Request.Id, pair.Request.TenantSlug, pair.Request.Full, pair.Request.CreatedAt, pair.Verification is null ? null : AuditVerificationResponse.From(pair.Verification)));
    }
}
