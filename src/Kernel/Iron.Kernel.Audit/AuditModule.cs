using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Api;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Audit;

/// <summary>The audit trail (TF-060, thin in S1): the append-only <c>kernel_audit.events</c> table, the writer, and the search endpoint. Hosts list it first.</summary>
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
