using FluentValidation;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iron.Kernel.Tenancy.Api;

/// <summary>A key officer's rotation: why, which both audit trails keep (TF-075 AC3).</summary>
public sealed record KeyRotationRequest(string? Reason);

public sealed class KeyRotationRequestValidator : AbstractValidator<KeyRotationRequest>
{
    public KeyRotationRequestValidator() => RuleFor(r => r.Reason).MaximumLength(KeyRotationRecord.ReasonMaxLength);
}

public sealed record TenantKeyResponse(string Key, int Version, int MinimumVersion, DateTimeOffset? RotatedAt, DateTimeOffset NextRotationAt, int RotationDays)
{
    public static TenantKeyResponse From(TenantKeyInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(key.Key, key.Version, key.MinimumVersion, key.RotatedAt, key.NextRotationAt, key.RotationDays);
    }
}

public sealed record KeyRotationResponse(
    Guid Id,
    string Key,
    string Trigger,
    string RequestedBy,
    string Reason,
    string State,
    int? FromVersion,
    int? ToVersion,
    DateTimeOffset RequestedAt,
    DateTimeOffset? RotatedAt,
    long Done,
    long Total,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? RetiredAt,
    string? Error)
{
    public static KeyRotationResponse From(KeyRotationInfo rotation)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        return new(rotation.Id, rotation.Key, rotation.Trigger, rotation.RequestedBy, rotation.Reason, rotation.State, rotation.FromVersion, rotation.ToVersion, rotation.RequestedAt, rotation.RotatedAt, rotation.Done, rotation.Total, rotation.FinishedAt, rotation.RetiredAt, rotation.Error);
    }
}

/// <summary>A tenant's keys and its latest rotations.</summary>
public sealed record TenantKeysResponse(IReadOnlyList<TenantKeyResponse> Keys, IReadOnlyList<KeyRotationResponse> Rotations);

/// <summary>A unit's re-encryption as it reports it: values moved, values to move, and whether every value has moved.</summary>
public sealed record KeyRotationProgress(long Done, long Total, bool Finished);

/// <summary>A unit's policy for its data and index keys: the days between their scheduled rotations.</summary>
public sealed record KeyPolicyRequest(int DataDays);

public sealed record KeyPolicyResponse(int DataDays);

/// <summary>
/// A tenant's keys in the control plane (TF-073, TF-075, ADR-0054). An operator reads them; a key officer rotates one with
/// a reason and a fresh token, which the fleet carries out within seconds. A unit's own client reads its rotations,
/// reports its re-encryption and sets its rotation policy, for its own tenant only.
/// </summary>
internal static class KeyEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var tenants = endpoints.MapApiGroup("tenants");
        tenants.MapGet("/{slug}/keys", StatusAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<TenantKeysResponse>(StatusCodes.Status200OK).WithName("TenantKeys").WithSummary("A tenant's keys and database password, their versions and schedules, and their latest rotations.");
        tenants.MapPost("/{slug}/keys/{key}/rotations", RotateAsync).RequirePermission(TenancyPermissions.KeysManage).RequireRecentAuthentication().Validate<KeyRotationRequest>().Produces<KeyRotationResponse>(StatusCodes.Status202Accepted).WithName("RotateTenantKey").WithSummary("Rotate a tenant's data, index or signing key, or its database password, with a reason and a fresh token; the fleet carries it out within seconds.");
        tenants.MapGet("/{slug}/keys/rotations/{id:guid}", RotationAsync).RequirePermission(TenancyPermissions.TenantsRead).Produces<KeyRotationResponse>(StatusCodes.Status200OK).WithName("TenantKeyRotation").WithSummary("One rotation of a tenant's key: where it stands and how far its re-encryption has come.");

        var units = endpoints.MapApiGroup("key-rotations");
        units.MapGet("/", OfCallerAsync).RequirePermission(TenancyPermissions.KeysReport).Produces<TenantKeysResponse>(StatusCodes.Status200OK).WithName("UnitKeyRotations").WithSummary("A unit's own keys and its rotations that are open or retired within a month.");
        units.MapPost("/{id:guid}/progress", ProgressAsync).RequirePermission(TenancyPermissions.KeysReport).Produces<KeyRotationResponse>(StatusCodes.Status200OK).WithName("ReportKeyRotationProgress").WithSummary("A unit reports how far its re-encryption has come, and when every value has moved.");
        units.MapPut("/policy", PolicyAsync).RequirePermission(TenancyPermissions.KeysReport).Produces<KeyPolicyResponse>(StatusCodes.Status200OK).WithName("SetKeyRotationPolicy").WithSummary("A unit sets the days between its data and index keys' scheduled rotations, kept within the operator's bounds.");
    }

    private static async Task<IResult> StatusAsync(string slug, KeyRotationService keys, CancellationToken cancellationToken)
    {
        var (found, rotations) = await keys.StatusAsync(slug, cancellationToken);
        return TypedResults.Ok(new TenantKeysResponse([.. found.Select(TenantKeyResponse.From)], [.. rotations.Select(KeyRotationResponse.From)]));
    }

    private static async Task<IResult> RotateAsync(string slug, string key, KeyRotationRequest? request, HttpContext http, KeyRotationService keys, CancellationToken cancellationToken)
    {
        var result = await keys.RequestAsync(slug, key, request?.Reason, cancellationToken);
        return ApiResults.From(result, http, rotation => TypedResults.Accepted($"{ApiRouting.Prefix}/tenants/{slug}/keys/rotations/{rotation.Id}", KeyRotationResponse.From(rotation)));
    }

    private static async Task<IResult> RotationAsync(string slug, Guid id, HttpContext http, KeyRotationService keys, CancellationToken cancellationToken)
    {
        var rotation = await keys.FindAsync(slug, id, cancellationToken);
        return rotation is null
            ? ApiResults.Problem(Error.NotFound(TenancyErrors.KeyRotationNotFound, $"Tenant '{slug}' has no key rotation {id}."), http)
            : TypedResults.Ok(KeyRotationResponse.From(rotation));
    }

    private static async Task<IResult> OfCallerAsync(HttpContext http, KeyRotationService keys, CancellationToken cancellationToken)
    {
        var result = await keys.OfCallerAsync(cancellationToken);
        return ApiResults.From(result, http, found => TypedResults.Ok(new TenantKeysResponse([.. found.Keys.Select(TenantKeyResponse.From)], [.. found.Rotations.Select(KeyRotationResponse.From)])));
    }

    private static async Task<IResult> ProgressAsync(Guid id, KeyRotationProgress progress, HttpContext http, KeyRotationService keys, CancellationToken cancellationToken)
    {
        var result = await keys.ProgressAsync(id, progress.Done, progress.Total, progress.Finished, cancellationToken);
        return ApiResults.From(result, http, rotation => TypedResults.Ok(KeyRotationResponse.From(rotation)));
    }

    private static async Task<IResult> PolicyAsync(KeyPolicyRequest policy, HttpContext http, KeyRotationService keys, CancellationToken cancellationToken)
    {
        var result = await keys.PolicyAsync(policy.DataDays, cancellationToken);
        return ApiResults.From(result, http, days => TypedResults.Ok(new KeyPolicyResponse(days)));
    }
}
