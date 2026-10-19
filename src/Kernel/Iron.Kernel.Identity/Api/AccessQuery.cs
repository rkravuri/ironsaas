using Iron.Kernel.Api.Problems;
using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Identity.Api;

/// <summary>Reads the scope of <c>GET /api/v1/me/access</c>: <c>unit</c>, or <c>record_type</c> and <c>record_id</c> with an optional <c>unit</c>.</summary>
internal static class AccessQuery
{
    public static Result<AccessScope> Parse(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var unitText = query["unit"].ToString();
        var recordType = query["record_type"].ToString().Trim();
        var recordText = query["record_id"].ToString();

        Guid? unit = null;
        if (unitText.Length > 0)
        {
            if (!Guid.TryParse(unitText, out var parsed) || parsed == Guid.Empty)
            {
                return Invalid("unit", "Give the id of an organisation unit.");
            }

            unit = parsed;
        }

        if (recordType.Length == 0 && recordText.Length == 0)
        {
            return unit is { } only ? AccessScope.Unit(only) : Invalid("unit", "Give unit, or record_type and record_id.");
        }

        if (recordType.Length == 0 || recordType.Length > 100)
        {
            return Invalid("record_type", "Give the record type with the record id, at most 100 characters.");
        }

        if (!Guid.TryParse(recordText, out var recordId) || recordId == Guid.Empty)
        {
            return Invalid("record_id", "Give the id of the record.");
        }

        return AccessScope.Record(recordType, recordId, unit);
    }

    private static Error Invalid(string field, string message) =>
        Error.Validation(ApiErrors.Validation, "The scope is not valid.", new FieldError(field, "invalid", message));
}
