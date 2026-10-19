using Iron.Kernel.Api.Problems;
using Iron.Kernel.Api.Responses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api.Permissions;

/// <summary>Endpoint metadata: the permission an operation needs. The start-up check and OpenAPI read it.</summary>
public sealed class PermissionMetadata(Permission permission)
{
    public Permission Permission { get; } = permission;
}

/// <summary>Endpoint metadata: any signed-in principal may call this operation (me, logout). Still explicit for the start-up check.</summary>
public sealed class SignedInMetadata;

public static class PermissionErrors
{
    public const string NotAuthenticated = "auth.not_authenticated";
    public const string SessionExpired = "auth.session_expired";
    public const string TokenInvalid = "auth.token_invalid";
    public const string TokenExpired = "auth.token_expired";
    public const string PermissionDenied = "authz.permission_denied";
}

/// <summary>
/// How an authentication middleware tells the filters why a request has no principal, so the 401 carries the right
/// code: a session that ran out gets <c>auth.session_expired</c> and the shell returns to the login page (TF-021).
/// </summary>
public static class AuthenticationFailure
{
    public const string ItemKey = "Iron.AuthenticationFailure";

    public static void Set(HttpContext context, string errorCode)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[ItemKey] = errorCode;
    }

    public static string? Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) ? value as string : null;
    }

    public static IResult Problem(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var code = Get(context) ?? PermissionErrors.NotAuthenticated;
        var message = code switch
        {
            PermissionErrors.SessionExpired => "Your session has ended. Sign in again.",
            PermissionErrors.TokenExpired => "The access token has expired. Request a new one.",
            PermissionErrors.TokenInvalid => "The access token is not valid.",
            _ => "Sign in to continue.",
        };
        context.Response.Headers.WWWAuthenticate = code is PermissionErrors.TokenExpired or PermissionErrors.TokenInvalid
            ? "Bearer error=\"invalid_token\""
            : "Bearer";
        return ApiResults.Problem(Error.Unauthorized(code, message), context);
    }
}

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Declares the permission of an endpoint or a whole group and checks it before the handler runs (TF-031).
    /// Nobody signed in gets 401; a principal without the permission gets 403 with <c>authz.permission_denied</c>.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Permission permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(permission);
        builder.WithMetadata(new PermissionMetadata(permission));
        builder.AddEndpointFilter(new PermissionFilter(permission));
        return builder;
    }

    /// <summary>Declares that any signed-in principal may call the endpoint, with no permission: <c>/api/v1/me</c>, logout.</summary>
    public static TBuilder RequireSignedIn<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new SignedInMetadata());
        builder.AddEndpointFilter(new SignedInFilter());
        return builder;
    }
}

public sealed class SignedInFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var principal = context.HttpContext.RequestServices.GetService<ICurrentPrincipal>();
        return principal is null || principal.IsAnonymous()
            ? AuthenticationFailure.Problem(context.HttpContext)
            : await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Runs before the handler of every endpoint that carries RequirePermission. The check is tenant-wide unless the
/// endpoint declares its scope with <c>ScopedToUnit</c> or <c>ScopedToRecord</c> (ADR-0030).
/// </summary>
public sealed class PermissionFilter(Permission permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var principal = http.RequestServices.GetService<ICurrentPrincipal>();
        if (principal is null || principal.IsAnonymous())
        {
            return AuthenticationFailure.Problem(http);
        }

        var evaluator = http.RequestServices.GetService<IPermissionEvaluator>();
        var scope = http.GetEndpoint()?.Metadata.GetMetadata<ScopeMetadata>() is { } scoped
            ? await scoped.ResolveAsync(http, http.RequestAborted).ConfigureAwait(false)
            : AccessScope.Tenant;
        var request = new AccessRequest(principal.Id, permission, Operation(http), http.Request.Path.Value ?? "/") { Scope = scope };
        var decision = evaluator is null
            ? AccessDecision.Deny(AccessDecision.NoEvaluator)
            : await evaluator.EvaluateAsync(request, http.RequestAborted).ConfigureAwait(false);

        if (decision.Allowed)
        {
            return await next(context).ConfigureAwait(false);
        }

        var problem = ProblemFactory.Create(Error.Forbidden(PermissionErrors.PermissionDenied, "You do not have the permission for this operation."), http);
        problem.Extensions["permission"] = permission.Name;
        return TypedResults.Problem(problem);
    }

    /// <summary>The operation name used in logs and audit events: <c>GET /api/v1/users</c>.</summary>
    public static string Operation(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.GetEndpoint() is RouteEndpoint route
            ? $"{http.Request.Method} {EndpointPermissionCheck.Route(route)}"
            : $"{http.Request.Method} {http.Request.Path}";
    }
}
