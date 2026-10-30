using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Visibility;
using Iron.Kernel.History;
using Iron.Kernel.Visibility.Api;
using Iron.Kernel.Visibility.Contracts;
using Iron.Kernel.Visibility.Data;
using Iron.Kernel.Visibility.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Visibility;

/// <summary>
/// Content visibility (TF-241, ADR-0057): the tenant's visibility tags and who holds them, the data library's
/// <see cref="IVisibilityTags"/> that the record policies narrow by, and the endpoints of a record's tags. Records carry
/// their own tags in their own tables. Needs Audit's writer, Authorization's principals and acting, and Identity's
/// security activity, through their contracts.
/// </summary>
public sealed class VisibilityModule : IModule
{
    public string Name => "visibility";

    public IReadOnlyList<Permission> Permissions => VisibilityPermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => VisibilityAuditEvents.Types;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddIronDbContext<VisibilityDbContext>();
        services.AddScoped<IVisibilityTags, VisibilityTagsSource>();
        services.AddScoped<TagService>();
        services.AddScoped<HoldingService>();
        services.AddScoped<IVisibilityTagHolders>(provider => provider.GetRequiredService<HoldingService>());
        services.AddScoped<IValidator<CreateTagRequest>, CreateTagRequestValidator>();
        services.AddScoped<IValidator<ChangeTagRequest>, ChangeTagRequestValidator>();
        services.AddScoped<IValidator<SetTagsRequest>, SetTagsRequestValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        VisibilityEndpoints.Map(endpoints);
    }
}
