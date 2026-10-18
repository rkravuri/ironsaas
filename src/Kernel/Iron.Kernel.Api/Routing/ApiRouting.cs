using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iron.Kernel.Api.Routing;

/// <summary>
/// Route conventions (TF-100): every operation lives under <c>/api/v1/{resource}</c>, the resource is a plural noun
/// in kebab-case (<c>users</c>, <c>purchase-orders</c>), and the group carries the resource as its OpenAPI tag.
/// </summary>
public static class ApiRouting
{
    public const string Prefix = "/api/v1";

    public static RouteGroupBuilder MapApiGroup(this IEndpointRouteBuilder endpoints, string resource)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!Naming.IsKebabCase(resource))
        {
            throw new ArgumentException($"'{resource}' is not a valid resource name. Use a plural noun in kebab-case, like purchase-orders.", nameof(resource));
        }

        return endpoints.MapGroup($"{Prefix}/{resource}").WithTags(resource);
    }
}
