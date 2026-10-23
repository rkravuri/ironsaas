using Iron.Kernel.Api.Modules;
using Iron.Kernel.Compliance.Api;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Compliance.Data;
using Iron.Kernel.Compliance.Services;
using Iron.Kernel.Data;
using Iron.Kernel.History;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Compliance;

/// <summary>
/// Compliance in the unit (TF-063, TF-219; ADR-0051): electronic signatures bound by hash to the version they sign, the
/// tenant's signing policy per document type, and the periodic reviews of the audit trail, always signed. Needs Audit's
/// writer and Settings' reader; Identity proves the signer through <see cref="ISignerAuthentication"/>, and a host
/// without it can sign nothing. Listed after the modules whose records it signs.
/// </summary>
public sealed class ComplianceModule : IModule
{
    public string Name => "compliance";

    public IReadOnlyList<Permission> Permissions => CompliancePermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => ComplianceAuditEvents.Types;

    public IReadOnlyList<SettingDefinition> Settings => ComplianceSettings.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddIronDbContext<ComplianceDbContext>();
        services.TryAddScoped<ISignerAuthentication, NoSignerAuthentication>();
        services.AddScoped<SignableCatalogue>();
        services.AddScoped<SigningPolicies>();
        services.AddScoped<SignatureProofs>();
        services.AddScoped<SignatureService>();
        services.AddScoped<ISignatures>(provider => provider.GetRequiredService<SignatureService>());
        services.AddScoped<AuditTrailReviews>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISignableRecords, AuditTrailReviewRecords>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ComplianceEndpoints.Map(endpoints);
    }
}
