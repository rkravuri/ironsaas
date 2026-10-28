using FluentValidation;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Keys.Contracts;
using Iron.Kernel.Keys.Data;
using Iron.Kernel.Keys.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Keys.Api;

/// <summary>A re-encryption as the Keys page shows it: the version the values move to, how far it has come, when it started and ended.</summary>
public sealed record ReencryptionResponse(Guid RotationId, int ToVersion, long Done, long Total, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

/// <summary>A key of the tenant, with the re-encryption of its latest rotation while it runs or until its old version is retired.</summary>
public sealed record KeyResponse(string Key, int Version, int MinimumVersion, DateTimeOffset? RotatedAt, DateTimeOffset NextRotationAt, int RotationDays, ReencryptionResponse? Reencryption);

public sealed record KeyRotationEntryResponse(Guid Id, string Key, string Trigger, string RequestedBy, string Reason, int FromVersion, int ToVersion, DateTimeOffset RotatedAt, long Done, long Total, DateTimeOffset? FinishedAt, DateTimeOffset? RetiredAt);

/// <summary>The tenant's keys, their latest rotations, newest first, and when the unit last heard from the control plane.</summary>
public sealed record KeysResponse(IReadOnlyList<KeyResponse> Keys, IReadOnlyList<KeyRotationEntryResponse> Rotations, DateTimeOffset? AskedAt);

public sealed record KeyCheckRequest(string? Label, string? Value);

public sealed class KeyCheckRequestValidator : AbstractValidator<KeyCheckRequest>
{
    public KeyCheckRequestValidator()
    {
        RuleFor(r => r.Label).MaximumLength(KeyCheck.LabelMaxLength);
        RuleFor(r => r.Value).MaximumLength(KeyCheck.ValueMaxLength);
    }
}

public sealed record KeyCheckSearchRequest(string? Value);

public sealed class KeyCheckSearchRequestValidator : AbstractValidator<KeyCheckSearchRequest>
{
    public KeyCheckSearchRequestValidator() => RuleFor(r => r.Value).MaximumLength(KeyCheck.ValueMaxLength);
}

public sealed record KeyCheckResponse(Guid Id, string Label, DateTimeOffset CreatedAt);

public sealed record KeyCheckMatchResponse(Guid Id, string Label, string Value, DateTimeOffset CreatedAt);

public sealed record KeyCheckMatchesResponse(IReadOnlyList<KeyCheckMatchResponse> Matches);

/// <summary>
/// The tenant's keys for its administrators (ADR-0054): read only, since rotation is the operator's; and key checks, a
/// value stored encrypted and found by its exact value, which the value travels to in a request's body, never its path.
/// </summary>
internal static class KeysEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var keys = endpoints.MapApiGroup("keys");
        keys.MapGet("/", ListAsync).RequirePermission(KeysPermissions.Read).Produces<KeysResponse>(StatusCodes.Status200OK).WithName("ListKeys").WithSummary("The tenant's keys: each version, its last and next rotation, and the running re-encryption's progress; and the latest rotations.");
        keys.MapPost("/checks", StoreCheckAsync).RequirePermission(KeysPermissions.ChecksManage).Validate<KeyCheckRequest>().Produces<KeyCheckResponse>(StatusCodes.Status201Created).WithName("StoreKeyCheck").WithSummary("Store a key check: a value kept encrypted with the tenant's data key and found again by its exact value.");
        keys.MapPost("/checks/search", FindChecksAsync).RequirePermission(KeysPermissions.ChecksManage).Validate<KeyCheckSearchRequest>().Produces<KeyCheckMatchesResponse>(StatusCodes.Status200OK).WithName("FindKeyChecks").WithSummary("Find key checks by their exact value, through the keyed hash; a part of a value finds nothing.");
    }

    private static async Task<IResult> ListAsync(KeysDbContext context, CancellationToken cancellationToken)
    {
        var order = new[] { "data", "index", "signing", "database" };
        var states = await context.States.AsNoTracking().ToListAsync(cancellationToken);
        var rotations = await context.Rotations.AsNoTracking().OrderByDescending(r => r.RotatedAt).Take(20).ToListAsync(cancellationToken);
        var keys = states
            .OrderBy(s => Array.IndexOf(order, s.Key))
            .Select(state =>
            {
                var latest = rotations.FirstOrDefault(r => r.Key == state.Key && r.RetiredAt is null && r.Key is "data" or "index");
                var reencryption = latest is null ? null : new ReencryptionResponse(latest.Id, latest.ToVersion, latest.Done, latest.Total, latest.StartedAt, latest.FinishedAt);
                return new KeyResponse(state.Key, state.KeyVersion, state.MinimumVersion, state.RotatedAt, state.NextRotationAt, state.RotationDays, reencryption);
            })
            .ToList();
        var askedAt = states.Count == 0 ? (DateTimeOffset?)null : states.Max(s => s.AskedAt);
        return TypedResults.Ok(new KeysResponse(keys, [.. rotations.Select(r => new KeyRotationEntryResponse(r.Id, r.Key, r.Trigger, r.RequestedBy, r.Reason, r.FromVersion, r.ToVersion, r.RotatedAt, r.Done, r.Total, r.FinishedAt, r.RetiredAt))], askedAt));
    }

    private static async Task<IResult> StoreCheckAsync(KeyCheckRequest request, HttpContext http, KeyChecks checks, CancellationToken cancellationToken)
    {
        var result = await checks.StoreAsync(request.Label, request.Value, cancellationToken);
        return ApiResults.From(result, http, check => TypedResults.Created($"{ApiRouting.Prefix}/keys/checks/{check.Id}", new KeyCheckResponse(check.Id, check.Label, check.CreatedAt)));
    }

    private static async Task<IResult> FindChecksAsync(KeyCheckSearchRequest request, KeyChecks checks, CancellationToken cancellationToken)
    {
        var found = await checks.FindAsync(request.Value ?? string.Empty, cancellationToken);
        return TypedResults.Ok(new KeyCheckMatchesResponse([.. found.Select(m => new KeyCheckMatchResponse(m.Id, m.Label, m.Value, m.CreatedAt))]));
    }
}
