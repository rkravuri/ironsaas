using Iron.Kernel.Api.Problems;
using Iron.Kernel.Authorization.Contracts;
using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Identity.Api;

/// <summary>
/// Where <c>GET /api/v1/me/access</c> asks: a scope to check at, or, with <c>include_children=true</c>, a unit's whole
/// subtree to hold over (<see cref="Over"/>), which is what changing an assignment to a unit and below needs (ADR-0031).
/// </summary>
internal sealed record AccessTarget(AccessScope Scope, RoleScope? Over);

/// <summary>
/// Reads the query of <c>GET /api/v1/me/access</c>: <c>unit</c>, with an optional <c>include_children</c>; or
/// <c>record_type</c> and <c>record_id</c> with an optional <c>unit</c>.
/// </summary>
internal static class AccessQuery
{
    public static Result<AccessTarget> Parse(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var unitText = query["unit"].ToString();
        var recordType = query["record_type"].ToString().Trim();
        var recordText = query["record_id"].ToString();
        var childrenText = query["include_children"].ToString();

        Guid? unit = null;
        if (unitText.Length > 0)
        {
            if (!Guid.TryParse(unitText, out var parsed) || parsed == Guid.Empty)
            {
                return Invalid("unit", "Give the id of an organisation unit.");
            }

            unit = parsed;
        }

        if (childrenText.Length > 0 && !bool.TryParse(childrenText, out _))
        {
            return Invalid("include_children", "Give true or false.");
        }

        var includeChildren = childrenText.Length > 0 && bool.Parse(childrenText);
        if (recordType.Length == 0 && recordText.Length == 0)
        {
            if (unit is not { } only)
            {
                return Invalid("unit", "Give unit, or record_type and record_id.");
            }

            return new AccessTarget(AccessScope.Unit(only), includeChildren ? RoleScope.Unit(only, includeChildren: true) : null);
        }

        if (includeChildren)
        {
            return Invalid("include_children", "The units below apply to a unit, not to a record.");
        }

        if (recordType.Length == 0 || recordType.Length > RoleScope.RecordTypeMaxLength)
        {
            return Invalid("record_type", $"Give the record type with the record id, at most {RoleScope.RecordTypeMaxLength} characters.");
        }

        if (!Guid.TryParse(recordText, out var recordId) || recordId == Guid.Empty)
        {
            return Invalid("record_id", "Give the id of the record.");
        }

        return new AccessTarget(AccessScope.Record(recordType, recordId, unit), null);
    }

    private static Error Invalid(string field, string message) =>
        Error.Validation(ApiErrors.Validation, "The scope is not valid.", new FieldError(field, "invalid", message));
}
