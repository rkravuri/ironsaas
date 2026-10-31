using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Seeding;
using Iron.Kernel.History;
using Iron.Kernel.Jobs.Api;
using Iron.Kernel.Jobs.Contracts;
using Iron.Kernel.Jobs.Data;
using Iron.Kernel.Jobs.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Jobs;

/// <summary>
/// Jobs (TF-120 to TF-124, ADR-0061): the unit's runs, coordinated through its database so any number of worker processes
/// run each occurrence once; the runner in the worker; the starter code uses; the job console and the one view of dead
/// letters; and the operator's command. Placement: no business meaning, so Kernel. The jobs themselves are the modules'
/// own, declared in their <c>IModule.Jobs</c>.
/// </summary>
public sealed class JobsModule : IModule
{
    public string Name => "jobs";

    public IReadOnlyList<Permission> Permissions => JobsPermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => JobsAuditEvents.Types;

    public IReadOnlyList<SettingDefinition> Settings => JobsSettings.All;

    public IReadOnlyList<JobDefinition> Jobs => [PruneRunsJob.Definition];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddIronDbContext<JobsDbContext>();
        services.AddSingleton(configuration.GetSection(RunnerOptions.SectionName).Get<RunnerOptions>() ?? new RunnerOptions());
        services.TryAddSingleton<JobsTelemetry>();
        services.TryAddSingleton<JobRunner>();
        services.AddScoped<IJobStarter, JobStarter>();
        services.AddScoped<JobConsole>();
        services.AddScoped<ISeeder, JobsSeeder>();
        services.AddSingleton(configuration.GetSection(JobsCommandOptions.SectionName).Get<JobsCommandOptions>() ?? new JobsCommandOptions());
        services.AddScoped<IUnitCommand, JobsCommand>();
        services.AddScoped<IValidator<JobReasonRequest>, JobReasonRequestValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        JobsEndpoints.Map(endpoints);
    }
}

public static class JobsHosting
{
    /// <summary>
    /// The runner (ADR-0061), in the unit's worker: any number of worker processes may run it, and the database decides
    /// which takes each run. The API only starts runs and shows them. Call it after AddModules.
    /// </summary>
    public static IServiceCollection AddJobRunner(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddJobHandlers();
        services.AddHostedService<JobRunnerService>();
        return services;
    }

    /// <summary>
    /// The handlers of the unit's jobs, which only a host that runs them resolves: their services are the worker's, so a
    /// host that declares a job but does not run it, as the API and the control plane do, never builds its handler.
    /// </summary>
    public static IServiceCollection AddJobHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var declarations = services.Select(d => d.ImplementationInstance).OfType<JobDeclarations>().LastOrDefault()
            ?? throw new InvalidOperationException("AddJobHandlers runs after AddModules, which declares the jobs.");
        foreach (var job in declarations.UnitJobs)
        {
            services.TryAddScoped(job.Definition.HandlerType);
        }

        return services;
    }
}
