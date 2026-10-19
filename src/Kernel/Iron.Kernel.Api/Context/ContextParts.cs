using Iron.Kernel.Api.Permissions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api.Context;

/// <summary>
/// One part of <c>GET /api/v1/me/context</c>, what the shell reads with the session and every 30 seconds (ADR-0034,
/// ADR-0035): entitlements, flags, and settings in A6. A module adds its part with <see cref="ContextPartExtensions.AddContextPart{TPart}"/>.
/// </summary>
public interface IContextPart
{
    /// <summary>The part's name in the answer, in snake_case: <c>entitlements</c>, <c>flags</c>.</summary>
    string Name { get; }

    /// <summary>What applies to the signed-in principal now.</summary>
    Task<object> BuildAsync(Guid principalId, CancellationToken cancellationToken);
}

/// <summary>Says that a host has context parts, so MapModules maps the endpoint once for all of them.</summary>
public sealed class ContextPartRegistration(string name)
{
    public string Name { get; } = name;
}

public static class ContextPartExtensions
{
    public const string Path = "/api/v1/me/context";

    /// <summary>Registers a part of <c>/me/context</c> (scoped).</summary>
    public static IServiceCollection AddContextPart<TPart>(this IServiceCollection services, string name)
        where TPart : class, IContextPart
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        services.AddSingleton(new ContextPartRegistration(name));
        services.AddScoped<IContextPart, TPart>();
        return services;
    }

    /// <summary>
    /// <c>GET /api/v1/me/context</c> for any signed-in principal: every registered part under its name, built in name
    /// order. MapModules maps it when a module registered a part.
    /// </summary>
    public static IEndpointRouteBuilder MapContext(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(Path, BuildAsync).RequireSignedIn().WithTags("me").WithName("MyContext").WithSummary("What applies to you in this tenant now: its entitlements and the flags (settings join in A6).");
        return endpoints;
    }

    private static async Task<IResult> BuildAsync(ICurrentPrincipal principal, IEnumerable<IContextPart> parts, CancellationToken cancellationToken)
    {
        var answer = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var part in parts.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            answer[part.Name] = await part.BuildAsync(principal.Id, cancellationToken);
        }

        return TypedResults.Ok(answer);
    }
}
