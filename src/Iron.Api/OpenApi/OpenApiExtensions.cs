using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scalar.AspNetCore;

namespace Iron.Api.OpenApi;

/// <summary>
/// OpenAPI from code (TF-100). The document is generated at <c>/openapi/v1.json</c> and browsed with Scalar at
/// <c>/scalar/v1</c>. Both are mapped in Development only: they are not on the anonymous allow-list (TF-030).
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
        });
        return services;
    }

    public static WebApplication MapIronOpenApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        return app;
    }
}
