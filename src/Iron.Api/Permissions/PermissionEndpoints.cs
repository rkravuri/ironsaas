using Iron.Api.Problems;
using Iron.Api.Responses;
using Iron.Core.Permissions;
using Iron.Core.Principals;
using Iron.Core.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Api.Permissions;

/// <summary>Endpoint metadata: the permission an operation needs. The start-up check and OpenAPI read it.</summary>
public sealed class PermissionMetadata(Permission permission)
{
    public Permission Permission { get; } = permission;
}

public static class PermissionErrors
{
    public const string NotAuthenticated = "auth.not_authenticated";
    public const string PermissionDenied = "authz.permission_denied";
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
}

/// <summary>Runs before the handler of every endpoint that carries RequirePermission.</summary>
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
            return ApiResults.Problem(Error.Unauthorized(PermissionErrors.NotAuthenticated, "Sign in to continue."), http);
        }

        var evaluator = http.RequestServices.GetService<IPermissionEvaluator>();
        var request = new AccessRequest(principal.Id, permission, Operation(http), http.Request.Path.Value ?? "/");
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
