using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Samples.Api;
using Iron.Kernel.Samples.Contracts;
using Iron.Kernel.Samples.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Samples;

/// <summary>
/// Sample records for testing a running unit (ADR-0057): the smoke and the browser tests meet the kernel's record
/// machinery, its policy, sharing, visibility tags and history, through a record type no product owns. The host loads it
/// only where the unit's configuration says <c>Samples:Enabled</c>, which the fleet writes for a tenant the operator
/// marks for testing; no other unit runs it, holds its permissions or has its schema.
/// </summary>
public sealed class SamplesModule : IModule
{
    public string Name => "samples";

    public IReadOnlyList<Permission> Permissions => SamplesPermissions.All;

    public IReadOnlyList<EventType> Events => SamplesEvents.All;

    public IReadOnlyList<EventSubscription> Subscriptions =>
    [
        EventSubscription.To<SampleRecordCreated, NoticeCreatedSample>(SamplesEvents.RecordCreated, NoticeCreatedSample.Subscription, "Marks a new sample record noticed, so the smoke sees an event handled end to end."),
    ];

    /// <summary>Whether a unit's configuration switches the module on.</summary>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return bool.TryParse(configuration[SamplesConfiguration.EnabledKey], out var enabled) && enabled;
    }

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIronDbContext<SamplesDbContext>();
        services.AddRecordPolicy(
            SampleRecord.RecordType,
            "Sample records",
            SamplesPermissions.RecordsRead,
            RecordRules.UnitVisibility<SampleRecord>(
                SamplesPermissions.RecordsRead,
                (RecordActions.Update, SamplesPermissions.RecordsManage),
                (RecordActions.Share, SamplesPermissions.RecordsManage),
                (RecordActions.Tag, SamplesPermissions.RecordsManage)));
        services.AddUnitRecordOwners<SamplesDbContext>(Name);
        services.AddScoped<IValidator<CreateSampleRecordRequest>, CreateSampleRecordRequestValidator>();
        services.AddScoped<IValidator<RenameSampleRecordRequest>, RenameSampleRecordRequestValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        SampleRecordsEndpoints.Map(endpoints);
    }
}
