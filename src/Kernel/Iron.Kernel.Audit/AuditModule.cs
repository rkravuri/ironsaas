using FluentValidation;
using Iron.Kernel.Api.ControlPlane;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Api;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.History;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.Files.Contracts;
using Iron.Kernel.History;
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

    public IReadOnlyList<AuditEventType> AuditEventTypes => [.. AuditEvents.Types, .. RecordAuditEvents.Types];

    /// <summary>The unit's sealing and retention (ADR-0061); the control plane seals in its own loop and keeps no archive.</summary>
    public IReadOnlyList<JobDefinition> Jobs => [AuditSealJob.Definition, AuditRetentionJob.Definition];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.AddIronDbContext<AuditDbContext>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuditTrail, AuditTrail>();

        // What the security alert rules count (TF-079, ADR-0056).
        services.AddScoped<IAuditCounts, AuditCounter>();
        services.AddScoped<IValidator<RetentionChoiceRequest>, RetentionChoiceRequestValidator>();
        services.AddScoped<IValidator<PlaceLegalHoldRequest>, PlaceLegalHoldRequestValidator>();
        services.AddScoped<IValidator<ReleaseLegalHoldRequest>, ReleaseLegalHoldRequestValidator>();

        // Field-level history (TF-062, ADR-0051): every tracked record's save becomes an event of the trail.
        services.AddScoped<IChangeRecorder, AuditChangeRecorder>();

        // The data library's record events (ADR-0038): it declares where they go, the audit trail is where.
        services.AddScoped<IRecordAudit, RecordAuditWriter>();

        // The chain (TF-061, ADR-0050): the worker seals and sends the anchors; the API reads the chain's state.
        services.AddScoped<AuditSealer>();
        var controlPlane = configuration.GetSection(ControlPlaneOptions.SectionName).Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();
        // One client per process, which the Keys module shares (ADR-0054): whichever registers it first.
        services.TryAddSingleton(controlPlane);
        services.TryAddSingleton<ControlPlaneClient>();
        services.AddSingleton<AnchorProgress>();
        services.AddScoped<AuditRestoreRecorder>();
        services.AddScoped<AuditImpersonators>();
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
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConfigurationChangeApplier, AuditChangeApplier>());

        // Evidence packs (TF-067, ADR-0052): the trail's part, the chain's head, and any record's state at a past time.
        services.AddScoped<IRecordHistory, RecordHistoryReader>();
        services.AddScoped<AuditEvidence>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IEvidenceSource, AuditEvidence>(provider => provider.GetRequiredService<AuditEvidence>()));
        services.AddScoped<IEvidenceChain>(provider => provider.GetRequiredService<AuditEvidence>());
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

/// <summary>Where a host's chain is anchored (ADR-0050, ADR-0053).</summary>
public enum AuditAnchoring
{
    /// <summary>A unit: its anchors go to the control plane, and its worker also archives and prunes the trail daily.</summary>
    ControlPlane,

    /// <summary>The control plane: its anchors go to its own anchor store; it keeps no archive.</summary>
    OwnStore,
}

public static class AuditSealingHosting
{
    /// <summary>
    /// The audit chain's sealing and anchoring: a unit's worker anchors to the control plane (ADR-0050), and its jobs
    /// <c>audit.seal_chain</c> and <c>audit.retention</c> seal and keep the trail (ADR-0061); the control plane's API
    /// anchors to its own store (ADR-0053), sealing in a loop of its own, since the control plane runs no job runner.
    /// </summary>
    public static IServiceCollection AddAuditSealing(this IServiceCollection services, IConfiguration configuration, AuditAnchoring anchoring = AuditAnchoring.ControlPlane)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(AuditSealingOptions.SectionName).Get<AuditSealingOptions>() ?? new AuditSealingOptions();
        services.AddSingleton(options);
        if (anchoring == AuditAnchoring.OwnStore)
        {
            // The control plane (ADR-0053): anchors to its store, which the host registers; no archive, no pruning.
            services.AddScoped<IAuditAnchorSink, AuditAnchorKeeper>();
            services.AddHostedService<AuditSealingWorker>();
            return services;
        }

        services.AddScoped<IAuditAnchorSink>(provider => provider.GetRequiredService<AuditAnchorSender>());
        return services;
    }
}
