using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Seeding;
using Iron.Kernel.History;
using Iron.Kernel.Tenancy.Api;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy;

/// <summary>
/// The tenant registry and the control plane's tenant API (TF-001, TF-002), and plans and tenants' entitlements with
/// their history (TF-040 to TF-045, ADR-0034). Needs the Fleet module for jobs and a secret store for the entitlements;
/// the control plane lists both.
/// </summary>
public sealed class TenancyModule : IModule
{
    public string Name => "tenancy";

    public IReadOnlyList<Permission> Permissions => TenancyPermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => TenancyAuditEvents.Types;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new TenancyOptions();
        configuration.GetSection(TenancyOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddIronDbContext<TenancyDbContext>();
        services.AddSingleton(UnitCatalogue.Load());
        services.AddScoped<TenantRegistry>();
        services.AddScoped<ITenantRegistry>(provider => provider.GetRequiredService<TenantRegistry>());
        services.AddScoped<PlanService>();
        services.AddScoped<EntitlementService>();
        services.AddScoped<EntitlementPublisher>();
        services.AddScoped<ITenantEntitlements>(provider => provider.GetRequiredService<EntitlementPublisher>());
        services.AddScoped<ISeeder, PlanSeeder>();
        services.AddScoped<FlagPublications>();
        services.AddScoped<FlagRuleService>();
        services.AddScoped<FlagPublisher>();
        services.AddScoped<ITenantFlags>(provider => provider.GetRequiredService<FlagPublisher>());
        services.AddScoped<FlagReportService>();
        services.AddScoped<IValidator<ReplaceFlagRulesRequest>, ReplaceFlagRulesRequestValidator>();
        services.AddScoped<IValidator<KillSwitchRequest>, KillSwitchRequestValidator>();
        services.AddScoped<IValidator<AuditRestoreAcceptance>, AuditRestoreAcceptanceValidator>();
        services.AddScoped<IValidator<CreateTenantRequest>, CreateTenantRequestValidator>();
        services.AddScoped<IValidator<AdministratorRecoveryRequest>, AdministratorRecoveryRequestValidator>();
        services.AddScoped<IValidator<SavePlanRequest>, SavePlanRequestValidator>();
        services.AddScoped<IValidator<ApplyPlanRequest>, ApplyPlanRequestValidator>();

        // A tenant's suites, added and removed by the operator and installed by the fleet (TF-244, ADR-0059).
        services.AddScoped<SuiteService>();
        services.AddScoped<IValidator<AddSuiteRequest>, AddSuiteRequestValidator>();
        services.AddScoped<IValidator<RemoveSuiteRequest>, RemoveSuiteRequestValidator>();

        // The operator's support and emergency access, and the platform's documents (TF-025, TF-026, TF-028, ADR-0049).
        services.AddScoped<OperatorNames>();
        services.AddScoped<OperatorImpersonation>();
        services.AddScoped<OperatorEvents>();
        services.AddScoped<IValidator<OperatorEventSearchRequest>, OperatorEventSearchRequestValidator>();
        services.AddScoped<IValidator<OperatorEventReplayRequest>, OperatorEventReplayRequestValidator>();
        services.AddScoped<BreakGlassService>();
        services.AddScoped<PlatformDocumentService>();
        services.AddScoped<AuditChainService>();
        services.AddScoped<ITenantAuditChains>(provider => provider.GetRequiredService<AuditChainService>());
        services.AddScoped<IValidator<AuditAnchorRequest>, AuditAnchorRequestValidator>();
        services.AddScoped<IValidator<OperatorImpersonationRequest>, OperatorImpersonationRequestValidator>();
        services.AddScoped<IValidator<BreakGlassRequest>, BreakGlassRequestValidator>();
        services.AddScoped<IValidator<PlatformDocumentRequest>, PlatformDocumentRequestValidator>();

        // The tenants' keys and database passwords, rotated by the fleet at a key officer's or the schedule's request (ADR-0054).
        services.AddSingleton(configuration.GetSection(KeyRotationOptions.SectionName).Get<KeyRotationOptions>() ?? new KeyRotationOptions());
        services.AddScoped<KeyRotationService>();
        services.AddScoped<ITenantKeys>(provider => provider.GetRequiredService<KeyRotationService>());
        services.AddScoped<IValidator<KeyRotationRequest>, KeyRotationRequestValidator>();

        // The units' security alerts as they arrive, and incidents with their deadlines (TF-079, ADR-0056).
        services.AddScoped<IValidator<AlertCopyRequest>, AlertCopyRequestValidator>();
        services.AddScoped<IncidentService>();
        services.AddScoped<IncidentEscalation>();
        services.AddScoped<IValidator<DeclareIncidentRequest>, DeclareIncidentRequestValidator>();
        services.AddScoped<IValidator<IncidentEntryRequest>, IncidentEntryRequestValidator>();
        services.AddScoped<IValidator<AffectedTenantRequest>, AffectedTenantRequestValidator>();
        services.AddScoped<IValidator<CloseIncidentRequest>, CloseIncidentRequestValidator>();
        services.AddScoped<IValidator<IncidentReviewRequest>, IncidentReviewRequestValidator>();

        var reconcile = new ReconcileOptions();
        configuration.GetSection(ReconcileOptions.SectionName).Bind(reconcile);
        services.AddSingleton(reconcile);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        TenantsEndpoints.Map(endpoints);
        EntitlementEndpoints.Map(endpoints);
        SuiteEndpoints.Map(endpoints);
        FlagEndpoints.Map(endpoints);
        OperatorAccessEndpoints.Map(endpoints);
        AuditChainEndpoints.Map(endpoints);
        KeyEndpoints.Map(endpoints);
        IncidentEndpoints.Map(endpoints);
    }
}

public static class TenancyWorkerExtensions
{
    /// <summary>The incidents' deadlines (ADR-0056): a review 10 days after the close, a notice 72 hours after a breach became known. The control plane calls it.</summary>
    public static IServiceCollection AddIncidentEscalation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<IncidentEscalationWorker>();
        return services;
    }

    /// <summary>
    /// The fleet worker's reconcile of the tenants' entitlements (ADR-0034): every minute, a tenant whose store copy is
    /// behind is written again. Only the fleet host calls it.
    /// </summary>
    public static IServiceCollection AddIronEntitlementReconciler(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<EntitlementReconciler>();
        return services;
    }

    /// <summary>
    /// The fleet worker's flag loops (ADR-0035): every minute the flag paths whose store copy is behind are written again,
    /// and once a week the flag report goes to the log (TF-052 AC1). Only the fleet host calls it.
    /// </summary>
    public static IServiceCollection AddIronFlagWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<FlagReconciler>();
        services.AddHostedService(provider => new FlagReportLogger(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<FlagReportLogger>>(), TimeSpan.FromDays(7)));
        return services;
    }

    /// <summary>
    /// The fleet worker's minute for break-glass (TF-026, ADR-0049): unapproved requests expire after 30 minutes, accesses
    /// close at their time limit, and the reviews of the closed ones come back. Only the fleet host calls it.
    /// </summary>
    public static IServiceCollection AddIronBreakGlassWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<BreakGlassWorker>();
        return services;
    }

    /// <summary>The flag report in the log once, when the control plane starts (TF-052).</summary>
    public static IServiceCollection AddIronFlagReportAtStart(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService(provider => new FlagReportLogger(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<FlagReportLogger>>()));
        return services;
    }
}
