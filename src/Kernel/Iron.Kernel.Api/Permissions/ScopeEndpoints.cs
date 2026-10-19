using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Api.Permissions;

/// <summary>
/// Endpoint metadata: how the permission filter finds where an operation happens, so a role assigned to a unit applies
/// to that unit's records and not to others (TF-033, ADR-0030). An endpoint without it is checked tenant-wide.
/// </summary>
public abstract class ScopeMetadata
{
    /// <summary>The scope of this request, or tenant-wide when the route value is missing or names nothing.</summary>
    public abstract ValueTask<AccessScope> ResolveAsync(HttpContext http, CancellationToken cancellationToken);

    protected static Guid? RouteId(HttpContext http, string routeValue)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.RouteValues.TryGetValue(routeValue, out var value) && Guid.TryParse(value?.ToString(), out var id) && id != Guid.Empty
            ? id
            : null;
    }
}

/// <summary>The route value is an organisation unit's id: <c>/organisation-units/{id}/rename</c>.</summary>
public sealed class UnitScopeMetadata(string routeValue) : ScopeMetadata
{
    public string RouteValue { get; } = routeValue;

    public override ValueTask<AccessScope> ResolveAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var unitId = RouteId(http, RouteValue);
        return ValueTask.FromResult(unitId is null ? AccessScope.Tenant : AccessScope.Unit(unitId.Value));
    }
}

/// <summary>
/// The route value is a record's id, and the lookup finds the unit that owns it (its <c>OwningOrganisationId</c>), for
/// example with one query on the module's context. A record that is not found is checked tenant-wide, so only a
/// tenant-wide grant reaches the handler that answers 404.
/// </summary>
public sealed class RecordScopeMetadata(string recordType, string routeValue, Func<IServiceProvider, Guid, CancellationToken, Task<Guid?>> owningUnit) : ScopeMetadata
{
    public string RecordType { get; } = recordType;

    public string RouteValue { get; } = routeValue;

    public override async ValueTask<AccessScope> ResolveAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var recordId = RouteId(http, RouteValue);
        if (recordId is null)
        {
            return AccessScope.Tenant;
        }

        var unitId = await owningUnit(http.RequestServices, recordId.Value, cancellationToken).ConfigureAwait(false);
        return unitId is null || unitId == Guid.Empty ? AccessScope.Tenant : AccessScope.Record(RecordType, recordId.Value, unitId.Value);
    }
}

public static class ScopeEndpointExtensions
{
    /// <summary>The permission of this endpoint is checked at the organisation unit named by a route value (default <c>id</c>).</summary>
    public static TBuilder ScopedToUnit<TBuilder>(this TBuilder builder, string routeValue = "id")
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeValue);
        return builder.WithMetadata(new UnitScopeMetadata(routeValue));
    }

    /// <summary>
    /// The permission of this endpoint is checked at the record named by a route value (default <c>id</c>): the lookup
    /// gives the unit that owns the record, so a role assigned to that unit, or to an ancestor with its children, applies.
    /// </summary>
    public static TBuilder ScopedToRecord<TBuilder>(this TBuilder builder, string recordType, Func<IServiceProvider, Guid, CancellationToken, Task<Guid?>> owningUnit, string routeValue = "id")
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        ArgumentNullException.ThrowIfNull(owningUnit);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeValue);
        return builder.WithMetadata(new RecordScopeMetadata(recordType, routeValue, owningUnit));
    }
}
