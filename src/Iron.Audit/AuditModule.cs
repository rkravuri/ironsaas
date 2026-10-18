using Iron.Api.Modules;
using Iron.Audit.Contracts;
using Iron.Audit.Services;
using Iron.Core.Permissions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Audit;

/// <summary>The audit trail module. T9 adds the <c>audit.events</c> table, the database writer and the read endpoint.</summary>
public sealed class AuditModule : IModule
{
    public string Name => "audit";

    public IReadOnlyList<Permission> Permissions => [];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IAuditWriter, LoggingAuditWriter>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
