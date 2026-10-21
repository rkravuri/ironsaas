using Iron.Kernel.Api.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scalar.AspNetCore;

namespace Iron.Kernel.Api.OpenApi;

/// <summary>
/// OpenAPI from code (TF-100). The document is generated at <c>/openapi/v1.json</c> and browsed with Scalar at
/// <c>/scalar/v1</c>. Both are mapped in Development only and carry the anonymous marker there; in other
/// environments they do not exist, so the anonymous allow-list stays short (TF-030). Every operation's description
/// starts with what it needs, its permission, a signed-in principal or nothing, and every mapping declares its success
/// responses with <c>.Produces&lt;T&gt;(status)</c>, so each operation carries its schemas (TF-100 AC3, the host test
/// holds both).
/// </summary>
public static class OpenApiExtensions
{
    /// <summary>The note of an operation any signed-in principal may call (<c>RequireSignedIn</c>).</summary>
    public const string AccessSignedIn = "Requires a signed-in principal; no permission.";

    /// <summary>The note of an operation on the anonymous allow-list (<c>AllowAnonymous</c>).</summary>
    public const string AccessAnonymous = "Anonymous: on the reviewed allow-list, no sign-in.";

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
                // Every operation says what it needs (TF-100 AC3): its permission, a signed-in principal, or nothing.
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var permission = metadata.OfType<PermissionMetadata>().LastOrDefault();
                var requirement = permission is not null ? $"Requires permission `{permission.Permission.Name}`."
                    : metadata.OfType<SignedInMetadata>().Any() ? AccessSignedIn
                    : metadata.OfType<IAllowAnonymous>().Any() ? AccessAnonymous
                    : null;
                if (requirement is not null)
                {
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
