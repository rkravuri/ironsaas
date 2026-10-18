using System.Text.Json;
using FluentValidation.Results;
using Iron.Kernel.Api.Problems;

namespace Iron.Kernel.Api.Validation;

/// <summary>
/// Turns FluentValidation failures into Foundation field errors: the property path becomes the snake_case JSON path
/// the client sent (<c>Items[0].UnitPrice</c> to <c>items[0].unit_price</c>) and the validator name becomes a short
/// code (<c>NotEmptyValidator</c> to <c>not_empty</c>).
/// </summary>
public static class FieldErrorMapper
{
    public static Error ToError(ValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var fields = result.Errors
            .Select(failure => new FieldError(ToFieldName(failure.PropertyName), ToCode(failure.ErrorCode), failure.ErrorMessage))
            .ToList();
        return Error.Validation(ApiErrors.Validation, "The request is not valid.", fields);
    }

    public static string ToFieldName(string? propertyPath)
    {
        if (string.IsNullOrEmpty(propertyPath))
        {
            return string.Empty;
        }

        var segments = propertyPath.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            var name = bracket < 0 ? segment : segment[..bracket];
            var indexer = bracket < 0 ? string.Empty : segment[bracket..];
            segments[i] = JsonNamingPolicy.SnakeCaseLower.ConvertName(name) + indexer;
        }

        return string.Join('.', segments);
    }

    public static string ToCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return "invalid";
        }

        const string suffix = "Validator";
        var name = errorCode.EndsWith(suffix, StringComparison.Ordinal) ? errorCode[..^suffix.Length] : errorCode;
        return JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
    }
}
