using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Api;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.Files.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Audit;

/// <summary>
/// The audit trail (TF-060): the append-only <c>kernel_audit.events</c> table, the writer and the search; its chain, anchors
/// and archive (TF-061, TF-064); legal holds and retention (TF-066, ADR-0050). Hosts list it first.
/// </summary>
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
        services.AddScoped<IAuditTrail, AuditTrail>();

        // The data library's record events (ADR-0038): it declares where they go, the audit trail is where.
        services.AddScoped<IRecordAudit, RecordAuditWriter>();

        // The chain (TF-061, ADR-0050): the worker seals and sends the anchors; the API reads the chain's state.
        services.AddScoped<AuditSealer>();
        var controlPlane = configuration.GetSection(ControlPlaneOptions.SectionName).Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();
        services.AddSingleton(controlPlane);
        services.AddSingleton<ControlPlaneClient>();
        services.AddSingleton<AnchorProgress>();
        services.AddScoped<AuditAnchorSender>();

        // Archive, retention and legal holds (TF-064, TF-066, ADR-0050): the worker runs them daily; the API serves them.
        // A host without the Files module keeps no files; the Files module, listed after Audit, registers the real storage.
        services.TryAddSingleton<IObjectStorage, NoObjectStorage>();
        var limits = configuration.GetSection(RetentionLimitsOptions.SectionName).Get<RetentionLimitsOptions>() ?? new RetentionLimitsOptions();
        services.AddSingleton(limits);
        services.AddScoped<RetentionPolicy>();
        services.AddScoped<IRetentionPeriods>(provider => provider.GetRequiredService<RetentionPolicy>());
        services.AddScoped<LegalHolds>();
        services.AddScoped<ILegalHolds>(provider => provider.GetRequiredService<LegalHolds>());
        services.AddScoped<IRecordArchive, BlobRecordArchive>();
        services.AddScoped<IRetentionAudit, RetentionAuditWriter>();
        services.AddScoped<RecordRetentionRunner>();
        services.AddScoped<AuditArchiver>();
        services.AddScoped<AuditPruner>();
        services.AddScoped<AuditArchiveSearch>();
        services.AddScoped<AuditExporter>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        AuditEventsEndpoints.Map(endpoints);
        AuditTrailEndpoints.Map(endpoints);
    }
}

public static class AuditSealingHosting
{
    /// <summary>The audit chain's sealing and anchoring loop, and the daily retention pass, for the unit's worker alone (ADR-0050).</summary>
    public static IServiceCollection AddAuditSealing(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(AuditSealingOptions.SectionName).Get<AuditSealingOptions>() ?? new AuditSealingOptions();
        services.AddSingleton(options);
        services.AddHostedService<AuditSealingWorker>();

        // The archive, the trail's pruning and the records' retention, once a day.
        var retention = configuration.GetSection(AuditRetentionOptions.SectionName).Get<AuditRetentionOptions>() ?? new AuditRetentionOptions();
        services.AddSingleton(retention);
        services.AddHostedService<AuditRetentionWorker>();
        return services;
    }
}
