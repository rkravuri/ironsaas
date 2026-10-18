using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Api.Permissions;

/// <summary>
/// The start-up check of TF-030: every endpoint carries RequirePermission or an explicit AllowAnonymous marker, every
/// declared permission is in the catalogue, and an evaluator is registered when anything is protected. The host does
/// not start otherwise; the message names the offending operations. Registered by AddIronApi, so no host can forget it.
/// It is a startup filter, not a hosted service: WebApplication starts hosted services before it builds the pipeline,
/// and the endpoints exist only once the pipeline is built.
/// </summary>
public sealed class EndpointPermissionCheck(ILogger<EndpointPermissionCheck> logger) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return app =>
        {
            next(app); // the host's pipeline; UseEndpoints registers every endpoint data source
            Run(app.ApplicationServices);
        };
    }

    public void Run(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints;
        var catalogue = services.GetService<PermissionCatalogue>();
        var evaluatorRegistered = services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IPermissionEvaluator));

        var violations = Check(endpoints, catalogue, evaluatorRegistered);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("The start-up permission check failed (TF-030):" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }

        var routes = endpoints.OfType<RouteEndpoint>().ToList();
        logger.LogInformation(
            "Permission check passed: {Protected} protected and {Anonymous} anonymous endpoint(s).",
            routes.Count(e => e.Metadata.GetMetadata<PermissionMetadata>() is not null),
            routes.Count(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null));
    }

    /// <summary>The pure rules, unit tested without a host.</summary>
    public static IReadOnlyList<string> Check(IEnumerable<Endpoint> endpoints, PermissionCatalogue? catalogue, bool evaluatorRegistered)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var violations = new List<string>();
        var anyProtected = false;
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var permission = endpoint.Metadata.GetMetadata<PermissionMetadata>();
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>();
            var name = Describe(endpoint);

            if (permission is null && anonymous is null)
            {
                violations.Add($"{name}: no permission and no anonymous marker. Add .RequirePermission(...) or .AllowAnonymous().");
                continue;
            }

            if (permission is not null && anonymous is not null)
            {
                violations.Add($"{name}: has both a permission and the anonymous marker. Keep one.");
            }

            if (permission is not null)
            {
                anyProtected = true;
                if (catalogue is not null && !catalogue.Contains(permission.Permission.Name))
                {
                    violations.Add($"{name}: permission '{permission.Permission.Name}' is not declared by any module of this host.");
                }
            }
        }

        if (anyProtected && !evaluatorRegistered)
        {
            violations.Add("Protected endpoints exist but no IPermissionEvaluator is registered. Add the Authorization module to the host.");
        }

        return violations;
    }

    /// <summary>The operation as logs, audit events and messages name it: <c>GET /api/v1/roles</c>; <c>ANY</c> when no method is restricted.</summary>
    public static string Describe(RouteEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        var method = methods is { Count: > 0 } ? string.Join(",", methods) : "ANY";
        return $"{method} {Route(endpoint)}";
    }

    /// <summary>The route pattern without a trailing slash: a group's root <c>/api/v1/roles/</c> reads <c>/api/v1/roles</c>.</summary>
    public static string Route(RouteEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return "/" + (endpoint.RoutePattern.RawText ?? string.Empty).Trim('/');
    }
}
