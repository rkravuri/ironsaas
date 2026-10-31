using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Events.Api;
using Iron.Kernel.Events.Contracts;
using Iron.Kernel.Events.Data;
using Iron.Kernel.Events.Services;
using Iron.Kernel.History;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Events;

/// <summary>
/// Domain events (TF-085, TF-110 to TF-113, ADR-0060): the outbox every module publishes into, in the transaction of its
/// change; the inbox that records what each subscriber handled; the dispatcher in the unit's worker; the inspection, the
/// dead letters and the replay; and the archive past the retention. Placement: no business meaning, so Kernel. The events
/// themselves are the modules' own, declared in their Contracts and listed in their <c>IModule.Events</c>.
/// </summary>
public sealed class EventsModule : IModule
{
    public string Name => "events";

    public IReadOnlyList<Permission> Permissions => EventsPermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => EventsAuditEvents.Types;

    public IReadOnlyList<SettingDefinition> Settings => EventsSettings.All;

    /// <summary>The unit's dispatch and archive (ADR-0061); the control plane dispatches in the fleet's loop and keeps its handled events.</summary>
    public IReadOnlyList<JobDefinition> Jobs => [EventDispatchJob.Definition, EventArchiveJob.Definition];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddIronDbContext<EventsDbContext>();
        services.AddSingleton(configuration.GetSection(DispatchOptions.SectionName).Get<DispatchOptions>() ?? new DispatchOptions());
        services.TryAddSingleton<EventsMetrics>();
        services.TryAddSingleton<EventDispatcher>();
        services.AddHttpContextAccessor();
        services.AddScoped<IEventPublisher, EventPublisher>();
        services.AddScoped<EventInspection>();
        services.AddScoped<IEventDeadLetters, EventDeadLetters>();
        services.AddSingleton(configuration.GetSection(EventsCommandOptions.SectionName).Get<EventsCommandOptions>() ?? new EventsCommandOptions());
        services.AddScoped<IUnitCommand, EventsCommand>();
        services.AddScoped<IValidator<ReplayEventsRequest>, ReplayEventsRequestValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        EventsEndpoints.Map(endpoints);
    }
}

public static class EventsHosting
{
    /// <summary>
    /// The control plane's dispatch loop, in the fleet (ADR-0060): the control plane runs no job runner, so its own events
    /// are handed on here; it has no files, so it keeps its handled events. A unit's worker hands its events on through
    /// its job <c>events.dispatch</c> (ADR-0061). A pass's hand-on takes an advisory lock, and a delivery locks its inbox
    /// row, so a second process changes nothing.
    /// </summary>
    public static IServiceCollection AddEventDispatch(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<EventDispatchWorker>();
        return services;
    }

    /// <summary>
    /// The archive of a unit's handled events (ADR-0060), which its job <c>events.archive</c> runs: in the unit's worker,
    /// which has the tenant's files.
    /// </summary>
    public static IServiceCollection AddEventArchive(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<EventArchiver>();
        return services;
    }
}
