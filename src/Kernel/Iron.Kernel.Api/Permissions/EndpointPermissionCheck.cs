using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Api.Permissions;

/// <summary>
/// The start-up check of TF-030: every endpoint carries RequirePermission, RequireSignedIn or an explicit AllowAnonymous marker, every
/// declared permission is in the catalogue, and an evaluator is registered when anything is protected; every required
/// feature is declared, and an entitlement gate is registered when anything is licensed (ADR-0034). The host does
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
        var isService = services.GetRequiredService<IServiceProviderIsService>();
        var evaluatorRegistered = isService.IsService(typeof(IPermissionEvaluator));
        var gateRegistered = isService.IsService(typeof(IEntitlementGate));

        var violations = Check(endpoints, catalogue, evaluatorRegistered, services.GetService<LicensingCatalogue>(), gateRegistered);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("The start-up permission check failed (TF-030):" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }

        var routes = endpoints.OfType<RouteEndpoint>().ToList();
        logger.LogInformation(
            "Permission check passed: {Protected} protected, {SignedIn} signed-in and {Anonymous} anonymous endpoint(s).",
            routes.Count(e => e.Metadata.GetMetadata<PermissionMetadata>() is not null),
            routes.Count(e => e.Metadata.GetMetadata<SignedInMetadata>() is not null),
            routes.Count(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null));
    }

    /// <summary>
    /// The pure rules, unit tested without a host. Beside the permissions (TF-030): every feature an endpoint requires is
    /// declared by a module of the host, and an entitlement gate is registered when anything is licensed (ADR-0034).
    /// </summary>
    public static IReadOnlyList<string> Check(IEnumerable<Endpoint> endpoints, PermissionCatalogue? catalogue, bool evaluatorRegistered, LicensingCatalogue? licensing = null, bool gateRegistered = true)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var violations = new List<string>();
        var anyProtected = false;
        var anyLicensed = false;
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            foreach (var feature in endpoint.Metadata.GetOrderedMetadata<FeatureMetadata>())
            {
                anyLicensed = true;
                if (licensing is not null && licensing.FindFeature(feature.Feature) is null)
                {
                    violations.Add($"{Describe(endpoint)}: feature '{feature.Feature}' is not declared by any module of this host.");
                }
            }

            anyLicensed |= endpoint.Metadata.GetMetadata<LicensedModuleMetadata>() is not null;

            var permission = endpoint.Metadata.GetMetadata<PermissionMetadata>();
            var signedIn = endpoint.Metadata.GetMetadata<SignedInMetadata>();
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>();
            var name = Describe(endpoint);

            if (permission is null && signedIn is null && anonymous is null)
            {
                violations.Add($"{name}: no permission and no anonymous marker. Add .RequirePermission(...), .RequireSignedIn() or .AllowAnonymous().");
                continue;
            }

            if (anonymous is not null && (permission is not null || signedIn is not null))
            {
                violations.Add($"{name}: has both a permission or sign-in requirement and the anonymous marker. Keep one.");
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

        if (anyLicensed && !gateRegistered)
        {
            violations.Add("Endpoints of a licensed module or with a feature exist but no IEntitlementGate is registered. Add the Entitlements module to the host.");
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
