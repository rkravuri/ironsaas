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

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddIronDbContext<EventsDbContext>();
        services.AddSingleton(configuration.GetSection(DispatchOptions.SectionName).Get<DispatchOptions>() ?? new DispatchOptions());
        services.AddSingleton(configuration.GetSection(ArchiveOptions.SectionName).Get<ArchiveOptions>() ?? new ArchiveOptions());
        services.TryAddSingleton<EventsMetrics>();
        services.TryAddSingleton<EventDispatcher>();
        services.AddHttpContextAccessor();
        services.AddScoped<IEventPublisher, EventPublisher>();
        services.AddScoped<EventInspection>();
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
    /// The dispatcher and the archive (ADR-0060): in the unit's worker, one per unit, and in the fleet for the control
    /// plane's own events. The API only publishes; a pass's hand-on takes an advisory lock anyway, and a delivery locks its
    /// inbox row, so a second worker changes nothing. The archive writes to the tenant's files, so a host without them, as
    /// the control plane is, keeps its handled events.
    /// </summary>
    public static IServiceCollection AddEventDispatch(this IServiceCollection services, bool archive = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<EventDispatchWorker>();
        if (archive)
        {
            services.AddScoped<EventArchiver>();
            services.AddHostedService<EventArchiveWorker>();
        }

        return services;
    }
}
