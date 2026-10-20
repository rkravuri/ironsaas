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
    public ValueTask<AccessScope> ResolveAsync(HttpContext http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        return ResolveAsync(http.RequestServices, name => http.Request.RouteValues.TryGetValue(name, out var value) ? value?.ToString() : null, cancellationToken);
    }

    /// <summary>
    /// The scope for these route values: a request's, or those an explanation names (ADR-0039), resolved the same way.
    /// Tenant-wide when the route value is missing or names nothing.
    /// </summary>
    public abstract ValueTask<AccessScope> ResolveAsync(IServiceProvider services, Func<string, string?> routeValue, CancellationToken cancellationToken);

    protected static Guid? RouteId(Func<string, string?> routeValue, string name)
    {
        ArgumentNullException.ThrowIfNull(routeValue);
        return Guid.TryParse(routeValue(name), out var id) && id != Guid.Empty ? id : null;
    }
}

/// <summary>The route value is an organisation unit's id: <c>/organisation-units/{id}/rename</c>.</summary>
public sealed class UnitScopeMetadata(string routeValue) : ScopeMetadata
{
    public string RouteValue { get; } = routeValue;

    public override ValueTask<AccessScope> ResolveAsync(IServiceProvider services, Func<string, string?> routeValue, CancellationToken cancellationToken)
    {
        var unitId = RouteId(routeValue, RouteValue);
        return ValueTask.FromResult(unitId is null ? AccessScope.Tenant : AccessScope.Unit(unitId.Value));
    }
}

/// <summary>
/// The route value is a record's id, and the lookup finds the unit that owns it (its <c>OwningOrganisationId</c>), for
/// example with one query on the module's context. A record that is not found is checked tenant-wide, so only a
/// tenant-wide grant reaches the handler that answers 404. <see cref="PolicyAction"/> names the record policy action the
/// handler opens the record for, so an explanation runs the same policy (ADR-0039); null when the handler applies none.
/// </summary>
public sealed class RecordScopeMetadata(string recordType, string routeValue, Func<IServiceProvider, Guid, CancellationToken, Task<Guid?>> owningUnit, string? policyAction = null) : ScopeMetadata
{
    public string RecordType { get; } = recordType;

    public string RouteValue { get; } = routeValue;

    public string? PolicyAction { get; } = policyAction;

    public override async ValueTask<AccessScope> ResolveAsync(IServiceProvider services, Func<string, string?> routeValue, CancellationToken cancellationToken)
    {
        var recordId = RouteId(routeValue, RouteValue);
        if (recordId is null)
        {
            return AccessScope.Tenant;
        }

        var unitId = await owningUnit(services, recordId.Value, cancellationToken).ConfigureAwait(false);
        return unitId is null || unitId == Guid.Empty ? AccessScope.Tenant : AccessScope.Record(RecordType, recordId.Value, unitId.Value);
    }
}

/// <summary>The endpoint keeps the rows its caller may see and answers 404 for the others (ADR-0031): the permission counts at any scope.</summary>
public sealed class RowScopeMetadata : ScopeMetadata
{
    public override ValueTask<AccessScope> ResolveAsync(IServiceProvider services, Func<string, string?> routeValue, CancellationToken cancellationToken) => ValueTask.FromResult(AccessScope.Anywhere);
}

public static class ScopeEndpointExtensions
{
    /// <summary>
    /// The endpoint filters its own rows by the caller's access (<c>IRecordAccess</c> and <c>WhereAccessible</c>) and
    /// answers 404 for a row outside it, so the filter only checks that the permission is held at some scope (ADR-0031).
    /// Never for an endpoint that changes one place: that one declares the place.
    /// </summary>
    public static TBuilder ScopedByRow<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new RowScopeMetadata());
    }

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
    /// <paramref name="policyAction"/> is the record policy action the handler opens the record for with
    /// <c>IRecordPolicies.FindAsync</c>, so an explanation of the endpoint runs that policy too (ADR-0039).
    /// </summary>
    public static TBuilder ScopedToRecord<TBuilder>(this TBuilder builder, string recordType, Func<IServiceProvider, Guid, CancellationToken, Task<Guid?>> owningUnit, string routeValue = "id", string? policyAction = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        ArgumentNullException.ThrowIfNull(owningUnit);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeValue);
        return builder.WithMetadata(new RecordScopeMetadata(recordType, routeValue, owningUnit, policyAction));
    }
}
