using Iron.Kernel.Api.Permissions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scalar.AspNetCore;

namespace Iron.Kernel.Api.OpenApi;

/// <summary>
/// OpenAPI from code (TF-100). The document is generated at <c>/openapi/v1.json</c> and browsed with Scalar at
/// <c>/scalar/v1</c>. Both are mapped in Development only and carry the anonymous marker there; in other
/// environments they do not exist, so the anonymous allow-list stays short (TF-030). Every operation's description
/// starts with the permission it needs.
/// </summary>
public static class OpenApiExtensions
{
    public static IServiceCollection AddIronOpenApi(this IServiceCollection services, string title)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                if (document.Info is not null)
                {
                    document.Info.Title = title;
                    document.Info.Version = "v1";
                }

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var permission = context.Description.ActionDescriptor.EndpointMetadata.OfType<PermissionMetadata>().LastOrDefault();
                if (permission is not null)
                {
                    var requirement = $"Requires permission `{permission.Permission.Name}`.";
                    operation.Description = string.IsNullOrEmpty(operation.Description) ? requirement : requirement + " " + operation.Description;
                }

                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static WebApplication MapIronOpenApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference().AllowAnonymous();
        }

        return app;
    }
}
