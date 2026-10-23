using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Iron.Kernel.History;

/// <summary>
/// The canonical JSON both hashed forms write (ADR-0050, ADR-0051): the audit event's and a signed record's snapshot. An
/// auditor recomputes either with their own tools from the rules in the Audit README:
/// <list type="bullet">
/// <item>Text in Unicode normalisation form C, with only <c>"</c>, <c>\</c> and the control characters escaped
/// (<c>\b \f \n \r \t</c>, the rest as <c>\u00xx</c>); everything else as itself, in UTF-8.</item>
/// <item>Identifiers in lowercase with hyphens; times in UTC as <c>yyyy-MM-ddTHH:mm:ss.ffffffZ</c>; a missing value as <c>null</c>.</item>
/// <item>An object with its keys in ordinal order, at every depth; a number as its decimal value without exponent or
/// trailing zeros; no spaces anywhere.</item>
/// </list>
/// </summary>
public static class CanonicalJson
{
    /// <summary>The time as the forms write it: UTC, microseconds, a Z.</summary>
    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    public static void Identifier(StringBuilder builder, Guid? id)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (id is { } value)
        {
            Text(builder, value.ToString("D"));
        }
        else
        {
            builder.Append("null");
        }
    }

    public static void Text(StringBuilder builder, string? value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (value is null)
        {
            builder.Append("null");
            return;
        }

        builder.Append('"');
        foreach (var character in value.Normalize(NormalizationForm.FormC))
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case < ' ':
                    builder.Append("\\u00").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>A JSON text, or <c>null</c> when there is none.</summary>
    public static void Json(StringBuilder builder, string? json)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(json))
        {
            builder.Append("null");
            return;
        }

        using var document = JsonDocument.Parse(json);
        Value(builder, document.RootElement);
    }

    public static void Value(StringBuilder builder, JsonElement element)
    {
        ArgumentNullException.ThrowIfNull(builder);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    Text(builder, property.Name);
                    builder.Append(':');
                    Value(builder, property.Value);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var head = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!head)
                    {
                        builder.Append(',');
                    }

                    head = false;
                    Value(builder, item);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                Text(builder, element.GetString());
                break;
            case JsonValueKind.Number:
                builder.Append(Number(element.GetRawText()));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    /// <summary>A number as its decimal value: no exponent, no trailing zeros after the point, no point when whole.</summary>
    public static string Number(string raw)
    {
        var value = decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        var text = value.ToString("0.############################", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }
}
