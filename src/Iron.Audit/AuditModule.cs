using Iron.Api.Modules;
using Iron.Audit.Api;
using Iron.Audit.Contracts;
using Iron.Audit.Data;
using Iron.Audit.Services;
using Iron.Core.Permissions;
using Iron.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Audit;

/// <summary>The audit trail (TF-060, thin in S1): the append-only <c>audit.events</c> table, the writer, and the search endpoint. Hosts list it first.</summary>
public sealed class AuditModule : IModule
{
    public string Name => "audit";

    public IReadOnlyList<Permission> Permissions => AuditPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.AddIronDbContext<AuditDbContext>();
        services.AddScoped<IAuditWriter, AuditWriter>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        AuditEventsEndpoints.Map(endpoints);
    }
}
