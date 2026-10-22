using System.Text.Json;
using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Tenancy.Api;

/// <summary>Whom the operator signs in as, why, and for how long (at most the tenant's maximum).</summary>
public sealed record OperatorImpersonationRequest(string LoginId, string Reason, int? Minutes);

public sealed class OperatorImpersonationRequestValidator : AbstractValidator<OperatorImpersonationRequest>
{
    public OperatorImpersonationRequestValidator()
    {
        RuleFor(r => r.LoginId).NotEmpty().MaximumLength(254);
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Say why: the person and the tenant's administrators read it.").MaximumLength(500);
        RuleFor(r => r.Minutes).InclusiveBetween(1, 120).When(r => r.Minutes is not null);
    }
}

/// <summary>Why, under which ticket, and for how long an operator needs emergency access to a tenant.</summary>
public sealed record BreakGlassRequest(string Reason, string Ticket, int? Minutes);

public sealed class BreakGlassRequestValidator : AbstractValidator<BreakGlassRequest>
{
    public BreakGlassRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Say why, in words the tenant's administrators will read.").MaximumLength(BreakGlassAccess.ReasonMaxLength);
        RuleFor(r => r.Ticket).NotEmpty().WithMessage("Name the ticket this access is for.").MaximumLength(BreakGlassAccess.TicketMaxLength);
        RuleFor(r => r.Minutes).InclusiveBetween(1, BreakGlassAccess.LongestMinutes).When(r => r.Minutes is not null);
    }
}

/// <summary>One action of a break-glass account, as its review lists it (TF-026 AC3).</summary>
public sealed record BreakGlassActionResponse(DateTimeOffset At, string EventType, string? Outcome, string? Target, string? RecordType, string? RecordId, string? Reason);

/// <summary>
/// A break-glass access as the control plane shows it (TF-026, ADR-0049): who asked and approved, why, the ticket, until
/// when, the account, its end, and its review; and once, to the operator who asked, the sign-in link.
/// </summary>
public sealed record BreakGlassResponse(
    Guid Id,
    string TenantSlug,
    string Status,
    string Reason,
    string Ticket,
    int Minutes,
    DateTimeOffset RequestedAt,
    string RequestedBy,
    DateTimeOffset ApprovalDeadline,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? AccessUntil,
    string Account,
    string? EndReason,
    DateTimeOffset? EndedAt,
    int? ReviewActions,
    IReadOnlyList<BreakGlassActionResponse>? Review = null,
    HandoverResponse? Handover = null)
{
    public static BreakGlassResponse From(BreakGlassAccess access, bool withReview = false, HandoverResponse? handover = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        IReadOnlyList<BreakGlassActionResponse>? review = null;
        if (withReview && access.Review is { } json)
        {
            review = [.. JsonDocument.Parse(json).RootElement.EnumerateArray().Select(a => new BreakGlassActionResponse(
                a.GetProperty("at").GetDateTimeOffset(),
                a.GetProperty("event_type").GetString() ?? string.Empty,
                Text(a, "outcome"),
                Text(a, "target"),
                Text(a, "record_type"),
                Text(a, "record_id"),
                Text(a, "reason")))];
        }

        return new BreakGlassResponse(
            access.Id,
            access.TenantSlug,
            access.Status.ToString().ToLowerInvariant(),
            access.Reason,
            access.Ticket,
            access.Minutes,
            access.RequestedAt,
            access.RequestedByName,
            access.ApprovalDeadline,
            access.ApprovedByName,
            access.ApprovedAt,
            access.AccessUntil,
            access.AccountLoginId,
            access.EndReason,
            access.EndedAt,
            access.ReviewActions,
            review,
            handover);
    }

    private static string? Text(JsonElement action, string name) =>
        action.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A new version of one of the platform's documents.</summary>
public sealed record PlatformDocumentRequest(string Key, string Title, string Body);

public sealed class PlatformDocumentRequestValidator : AbstractValidator<PlatformDocumentRequest>
{
    public PlatformDocumentRequestValidator()
    {
        RuleFor(r => r.Key).NotEmpty().MaximumLength(PlatformDocument.KeyMaxLength);
        RuleFor(r => r.Title).NotEmpty().MaximumLength(PlatformDocument.TitleMaxLength);
        RuleFor(r => r.Body).NotEmpty().MaximumLength(PlatformDocument.BodyMaxLength);
    }
}

public sealed record PlatformDocumentResponse(string Key, int Version, string Title, string Body, DateTimeOffset PublishedAt);

public sealed record PlatformDocumentsResponse(IReadOnlyList<PlatformDocumentResponse> Documents);

/// <summary>
/// The operator's support and emergency access, and the platform's documents (TF-025, TF-026, TF-028, ADR-0049): signing in
/// as a person of a tenant that allows it, break-glass with two operators, and the terms every person accepts.
/// </summary>
internal static class OperatorAccessEndpoints
{
    public static readonly ListFields<BreakGlassAccess> Fields = new ListFields<BreakGlassAccess>()
        .Add("id", a => a.Id, FieldUse.Select | FieldUse.Filter)
        .Add("tenant_slug", a => a.TenantSlug, FieldUse.Select | FieldUse.Filter)
        .Add("status", a => a.Status, FieldUse.Select | FieldUse.Filter)
        .Add("ticket", a => a.Ticket, FieldUse.Select | FieldUse.Filter)
        .Add("requested_at", a => a.RequestedAt, FieldUse.Select | FieldUse.Sort | FieldUse.Filter)
        .SortByDefault("requested_at", SortDirection.Descending);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var tenants = endpoints.MapApiGroup("tenants");
        tenants.MapPost("/{slug}/impersonation", ImpersonateAsync).RequirePermission(TenancyPermissions.ImpersonationStart).RequireRecentAuthentication().Validate<OperatorImpersonationRequest>().Produces<ProvisioningResponse>(StatusCodes.Status202Accepted).WithName("ImpersonateInTenant").WithSummary("Ask the fleet for a one-time link to sign in as a person of a tenant that allows it, with a reason; the provisioning report gives it once to you.");
        tenants.MapPost("/{slug}/break-glass", RequestAsync).RequirePermission(TenancyPermissions.BreakGlassRequest).Validate<BreakGlassRequest>().Produces<BreakGlassResponse>(StatusCodes.Status201Created).WithName("RequestBreakGlass").WithSummary("Ask for emergency access to a tenant, with a reason, a ticket and a duration; a second operator approves within 30 minutes.");

        var breakGlass = endpoints.MapApiGroup("break-glass");
        breakGlass.MapGet("/", ListAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<ListResponse<BreakGlassResponse>>(StatusCodes.Status200OK).WithName("ListBreakGlass").WithSummary("The break-glass requests and accesses, newest first.");
        breakGlass.MapGet("/{id:guid}", GetAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<BreakGlassResponse>(StatusCodes.Status200OK).WithName("GetBreakGlass").WithSummary("One break-glass access with its review; once it is open, its sign-in link, once, to the operator who asked.");
        breakGlass.MapPost("/{id:guid}/approve", ApproveAsync).RequirePermission(TenancyPermissions.BreakGlassApprove).RequireRecentAuthentication().Produces<BreakGlassResponse>(StatusCodes.Status200OK).WithName("ApproveBreakGlass").WithSummary("Approve another operator's break-glass request, with a fresh token; the fleet opens the account.");
        breakGlass.MapPost("/{id:guid}/end", EndAsync).RequirePermission(TenancyPermissions.BreakGlassRequest).Produces<BreakGlassResponse>(StatusCodes.Status200OK).WithName("EndBreakGlass").WithSummary("End an access before its time; the fleet closes the account and the review follows.");

        var documents = endpoints.MapApiGroup("platform-documents");
        documents.MapGet("/", DocumentsAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<PlatformDocumentsResponse>(StatusCodes.Status200OK).WithName("ListPlatformDocuments").WithSummary("The current versions of the platform's terms of service and privacy notice.");
        documents.MapPost("/", PublishAsync).RequirePermission(TenancyPermissions.DocumentsManage).Validate<PlatformDocumentRequest>().Produces<PlatformDocumentResponse>(StatusCodes.Status201Created).WithName("PublishPlatformDocument").WithSummary("Publish a new version of the terms of service or the privacy notice; every person accepts it at their next request.");
    }

    private static async Task<IResult> ImpersonateAsync(string slug, OperatorImpersonationRequest request, HttpContext http, OperatorImpersonation impersonation, CancellationToken cancellationToken)
    {
        var result = await impersonation.RequestAsync(slug, request.LoginId, request.Reason, request.Minutes, cancellationToken);
        return ApiResults.From(result, http, job => TypedResults.Accepted($"{ApiRouting.Prefix}/tenants/{slug}/provisioning", ProvisioningResponse.From(job)));
    }

    private static async Task<IResult> RequestAsync(string slug, BreakGlassRequest request, HttpContext http, BreakGlassService breakGlass, CancellationToken cancellationToken)
    {
        var result = await breakGlass.RequestAsync(slug, request.Reason, request.Ticket, request.Minutes, cancellationToken);
        return ApiResults.From(result, http, access => TypedResults.Created($"{ApiRouting.Prefix}/break-glass/{access.Id}", BreakGlassResponse.From(access)));
    }

    private static async Task<IResult> ListAsync(HttpContext http, TenancyDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.BreakGlassAccesses.AsNoTracking().ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(access => BreakGlassResponse.From(access)), http, query);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, BreakGlassService breakGlass, TenantRegistry registry, CancellationToken cancellationToken)
    {
        var found = await breakGlass.FindAsync(id, cancellationToken);
        if (found.IsFailure)
        {
            return ApiResults.Problem(found.Error, http);
        }

        var view = found.Value;
        HandoverResponse? handover = null;
        if (view.LinkState == HandoverState.Given && view.Link is { } link && await registry.FindAsync(view.Access.TenantSlug, cancellationToken) is { } tenant)
        {
            // The link once, to the operator who asked (ADR-0046); no cache keeps the answer.
            handover = new HandoverResponse("given", link.Kind, link.Subject, $"https://{tenant.Hostnames[0]}{link.LinkPath}", link.ExpiresAt);
            http.Response.Headers.CacheControl = "no-store";
        }
        else if (view.LinkState is HandoverState.Taken or HandoverState.Expired or HandoverState.NotYours)
        {
            handover = new HandoverResponse(view.LinkState switch
            {
                HandoverState.Taken => "taken",
                HandoverState.Expired => "expired",
                _ => "not_yours",
            });
        }

        return TypedResults.Ok(BreakGlassResponse.From(view.Access, withReview: true, handover));
    }

    private static async Task<IResult> ApproveAsync(Guid id, HttpContext http, BreakGlassService breakGlass, CancellationToken cancellationToken)
    {
        var result = await breakGlass.ApproveAsync(id, cancellationToken);
        return ApiResults.From(result, http, access => TypedResults.Ok(BreakGlassResponse.From(access)));
    }

    private static async Task<IResult> EndAsync(Guid id, HttpContext http, BreakGlassService breakGlass, CancellationToken cancellationToken)
    {
        var result = await breakGlass.EndAsync(id, cancellationToken);
        return ApiResults.From(result, http, access => TypedResults.Ok(BreakGlassResponse.From(access)));
    }

    private static async Task<IResult> DocumentsAsync(PlatformDocumentService documents, CancellationToken cancellationToken)
    {
        var current = await documents.CurrentAsync(cancellationToken);
        return TypedResults.Ok(new PlatformDocumentsResponse([.. current.Select(d => new PlatformDocumentResponse(d.Key, d.Number, d.Title, d.Body, d.PublishedAt))]));
    }

    private static async Task<IResult> PublishAsync(PlatformDocumentRequest request, HttpContext http, PlatformDocumentService documents, CancellationToken cancellationToken)
    {
        var result = await documents.PublishAsync(request.Key, request.Title, request.Body, cancellationToken);
        return ApiResults.From(result, http, d => TypedResults.Created($"{ApiRouting.Prefix}/platform-documents/{d.Key}", new PlatformDocumentResponse(d.Key, d.Number, d.Title, d.Body, d.PublishedAt)));
    }
}
