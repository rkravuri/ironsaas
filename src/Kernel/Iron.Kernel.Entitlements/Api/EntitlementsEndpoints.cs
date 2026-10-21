using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Entitlements.Data;
using Iron.Kernel.Entitlements.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Entitlements.Api;

/// <summary>One module or feature as the shell needs it: what applies to the signed-in person now, and why.</summary>
public sealed record ContextEntry(string Code, EntitlementStatus Status, string Reason);

/// <summary>The entitlements part of <c>/me/context</c>: the answer's revision and age, and every licensed module and feature of this build.</summary>
public sealed record ContextEntitlements(int Revision, string? Plan, DateTimeOffset? ConfirmedAt, bool Stale, IReadOnlyList<ContextEntry> Modules, IReadOnlyList<ContextEntry> Features);


public sealed record SeatsResponse(int? Limit, int Assigned, int? Remaining);

/// <summary>Every per-person module's seat usage under <c>data</c>: one row per module, so no list envelope.</summary>
public sealed record SeatUsageResponse(IReadOnlyList<SeatUsage> Data);

public sealed record SubscriptionModule(string Code, string Module, SubscriptionType SubscriptionType, EntitlementStatus Status, string Reason, DateOnly? StartsOn, DateOnly? EndsOn, DateOnly? GraceEndsOn, SeatsResponse? Seats);

public sealed record SubscriptionFeature(string Code, string Description, string Module, string? Licence, EntitlementStatus Status, string Reason, DateOnly? StartsOn, DateOnly? EndsOn, DateOnly? GraceEndsOn);

/// <summary>The tenant's subscription for its administrators (the console's Subscription page): read only, plans are the operator's.</summary>
public sealed record SubscriptionResponse(string? Plan, int Revision, DateTimeOffset? ConfirmedAt, bool Stale, int GraceDays, IReadOnlyList<SubscriptionModule> Modules, IReadOnlyList<SubscriptionFeature> Features);

public sealed record SeatResponse(Guid Id, string Module, Guid PrincipalId, DateTimeOffset CreatedAt)
{
    public static SeatResponse From(SeatAssignment seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        return new SeatResponse(seat.Id, seat.ModuleCode, seat.PrincipalId, seat.CreatedAt);
    }
}

public sealed record AssignSeatRequest(string Module, Guid PrincipalId);

public sealed class AssignSeatRequestValidator : AbstractValidator<AssignSeatRequest>
{
    public AssignSeatRequestValidator()
    {
        RuleFor(r => r.Module).NotEmpty().Must(ModuleLicence.IsValidCode).WithMessage("Use the module's licence code.");
        RuleFor(r => r.PrincipalId).NotEmpty();
    }
}

/// <summary>The unit's entitlement API: the subscription and seats (TF-041, TF-043); the shell's part of /me/context is <see cref="EntitlementsContextPart"/>.</summary>
internal static class EntitlementsEndpoints
{
    public static readonly ListFields<SeatAssignment> SeatFields = new ListFields<SeatAssignment>()
        .Add("id", s => s.Id, FieldUse.Select | FieldUse.Filter)
        .Add("module", s => s.ModuleCode, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("principal_id", s => s.PrincipalId, FieldUse.Select | FieldUse.Filter)
        .Add("created_at", s => s.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("created_at");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapApiGroup("subscription").MapGet("/", Subscription).RequirePermission(EntitlementsPermissions.SubscriptionRead).Produces<SubscriptionResponse>(StatusCodes.Status200OK).WithName("GetSubscription").WithSummary("The tenant's licensed modules and features with status, dates and reason, and seat usage.");

        var seats = endpoints.MapApiGroup("seats");
        seats.MapGet("/", ListSeatsAsync).RequirePermission(EntitlementsPermissions.SeatsRead).Produces<ListResponse<SeatResponse>>(StatusCodes.Status200OK).WithName("ListSeats").WithSummary("The seats of per-person modules; filter=module:eq:<code>.");
        seats.MapGet("/usage", UsageAsync).RequirePermission(EntitlementsPermissions.SeatsRead).Produces<SeatUsageResponse>(StatusCodes.Status200OK).WithName("SeatUsage").WithSummary("Per per-person module: the licensed count, the seats assigned and the seats left.");
        seats.MapPost("/", AssignAsync).RequirePermission(EntitlementsPermissions.SeatsManage).Validate<AssignSeatRequest>().Produces<SeatResponse>(StatusCodes.Status201Created).WithName("AssignSeat").WithSummary("Give a person a seat of a per-person module, within the licensed count.");
        seats.MapDelete("/{id:guid}", UnassignAsync).RequirePermission(EntitlementsPermissions.SeatsManage).Produces(StatusCodes.Status204NoContent).WithName("RemoveSeat").WithSummary("Take a seat back; the person loses the module on the next request.");
    }

    private static async Task<IResult> Subscription(EntitlementEvaluator entitlements, EntitlementCache cache, LicensingCatalogue catalogue, SeatService seats, CancellationToken cancellationToken)
    {
        var current = cache.Current;
        var document = current?.Document;
        var graceDays = document?.GraceDays ?? 0;
        var usage = (await seats.UsageAsync(cancellationToken)).ToDictionary(u => u.Module, StringComparer.Ordinal);

        // Every licensed module this build has, and any the document names that this build does not: the page shows both.
        var codes = catalogue.Modules.Select(m => m.Code).Union(document?.Modules.Select(m => m.Code) ?? [], StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var modules = codes.Select(code =>
        {
            var declared = catalogue.FindModule(code);
            var entry = document?.FindModule(code);
            var entitlement = entitlements.Module(code);
            var seat = usage.GetValueOrDefault(code);
            return new SubscriptionModule(
                code,
                declared?.Module ?? code,
                declared?.SubscriptionType ?? entry?.SubscriptionType ?? SubscriptionType.Platform,
                entitlement.Status,
                entitlement.Reason,
                StartsOn(entry?.StartsOn),
                entry?.EndsOn,
                entry?.EndsOn is { } end ? EntitlementRules.GraceEnds(end, graceDays) : null,
                seat is null ? null : new SeatsResponse(seat.Limit, seat.Assigned, seat.Remaining));
        }).ToList();

        var features = catalogue.Features.Select(feature =>
        {
            var entry = document?.FindFeature(feature.Code);
            var entitlement = entitlements.Feature(feature.Code);
            return new SubscriptionFeature(feature.Code, feature.Description, feature.Module, feature.Licence, entitlement.Status, entitlement.Reason, StartsOn(entry?.StartsOn), entry?.EndsOn, entry?.EndsOn is { } end ? EntitlementRules.GraceEnds(end, graceDays) : null);
        }).ToList();

        return TypedResults.Ok(new SubscriptionResponse(document?.Plan, document?.Revision ?? 0, current?.ConfirmedAt, entitlements.Snapshot is null, graceDays, modules, features));
    }

    private static async Task<IResult> ListSeatsAsync(HttpContext http, EntitlementsDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, SeatFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Seats.AsNoTracking().ApplyListQuery(query, SeatFields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(SeatResponse.From), http, query);
    }

    private static async Task<IResult> UsageAsync(SeatService seats, CancellationToken cancellationToken) =>
        TypedResults.Ok(new SeatUsageResponse(await seats.UsageAsync(cancellationToken)));

    private static async Task<IResult> AssignAsync(AssignSeatRequest request, HttpContext http, SeatService seats, CancellationToken cancellationToken)
    {
        var result = await seats.AssignAsync(request.Module, request.PrincipalId, cancellationToken);
        return ApiResults.From(result, http, seat => TypedResults.Created($"{ApiRouting.Prefix}/seats/{seat.Id}", SeatResponse.From(seat)));
    }

    private static async Task<IResult> UnassignAsync(Guid id, HttpContext http, SeatService seats, CancellationToken cancellationToken)
    {
        var result = await seats.UnassignAsync(id, cancellationToken);
        return ApiResults.From(result, http, () => TypedResults.NoContent());
    }

    /// <summary>The configuration source starts from the beginning of time; the page shows no start for it.</summary>
    private static DateOnly? StartsOn(DateOnly? startsOn) => startsOn is { } start && start != DateOnly.MinValue ? start : null;
}
