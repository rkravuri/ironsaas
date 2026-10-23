using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Iron.Kernel.Audit.Contracts;

/// <summary>
/// The content of one audit event as the chain hashes it (TF-061, ADR-0050): every column a person or module wrote, and
/// its place in the chain. The sealing columns themselves (previous hash, hash, version, sealing time) are not content.
/// </summary>
public sealed record ChainedEvent(
    Guid Id,
    long Position,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    string EventType,
    string Outcome,
    Guid ActorId,
    Guid? OnBehalfOfId,
    Guid? ImpersonatorId,
    string Source,
    string Severity,
    string? Target,
    string? RecordType,
    Guid? RecordId,
    string? Reason,
    string? CorrelationId,
    string? Details);

/// <summary>
/// The canonical form of an audit event, version 1, and its hash (TF-061, ADR-0050). Documented field by field in the
/// Audit README so an auditor can recompute any hash with their own tools:
/// <list type="bullet">
/// <item>A JSON array, written compactly, of: the text <c>iron-audit-v1</c>, the id, the position, the time, the creator,
/// the event type, the outcome, the actor, the person acted for, the impersonator, the source, the severity, the target,
/// the record type, the record id, the reason, the correlation id, the details, and the previous event's hash.</item>
/// <item>Identifiers in lowercase with hyphens; the time in UTC as <c>yyyy-MM-ddTHH:mm:ss.ffffffZ</c> (the database keeps
/// microseconds); the position as a whole number; a missing value as <c>null</c>.</item>
/// <item>Text in Unicode normalisation form C, with only <c>"</c>, <c>\</c> and the control characters escaped
/// (<c>\b \f \n \r \t</c>, the rest as <c>\u00xx</c>); everything else as itself, in UTF-8.</item>
/// <item>The details as a JSON object with its keys in ordinal order, at every depth; a number as its decimal value
/// without exponent or trailing zeros.</item>
/// <item>The hash is SHA-256 of those UTF-8 bytes, in lowercase hexadecimal. The first event's previous hash is 64 zeros.</item>
/// </list>
/// </summary>
public static class AuditCanonicalForm
{
    /// <summary>The version a hash was made with, kept with every sealed event, so a later form can sit beside this one.</summary>
    public const short Version = 1;

    /// <summary>The first word of the array, which names the form and its version.</summary>
    public const string Label = "iron-audit-v1";

    /// <summary>The previous hash of position 1.</summary>
    public static readonly string Genesis = new('0', 64);

    public static string Write(ChainedEvent chained, string previousHash)
    {
        ArgumentNullException.ThrowIfNull(chained);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousHash);
        var builder = new StringBuilder(512);
        builder.Append('[');
        Text(builder, Label);
        Separate(builder);
        Identifier(builder, chained.Id);
        Separate(builder);
        builder.Append(chained.Position.ToString(CultureInfo.InvariantCulture));
        Separate(builder);
        Text(builder, Time(chained.CreatedAt));
        Separate(builder);
        Identifier(builder, chained.CreatedBy);
        Separate(builder);
        Text(builder, chained.EventType);
        Separate(builder);
        Text(builder, chained.Outcome);
        Separate(builder);
        Identifier(builder, chained.ActorId);
        Separate(builder);
        Identifier(builder, chained.OnBehalfOfId);
        Separate(builder);
        Identifier(builder, chained.ImpersonatorId);
        Separate(builder);
        Text(builder, chained.Source);
        Separate(builder);
        Text(builder, chained.Severity);
        Separate(builder);
        Text(builder, chained.Target);
        Separate(builder);
        Text(builder, chained.RecordType);
        Separate(builder);
        Identifier(builder, chained.RecordId);
        Separate(builder);
        Text(builder, chained.Reason);
        Separate(builder);
        Text(builder, chained.CorrelationId);
        Separate(builder);
        Details(builder, chained.Details);
        Separate(builder);
        Text(builder, previousHash);
        builder.Append(']');
        return builder.ToString();
    }

    public static string Hash(ChainedEvent chained, string previousHash) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Write(chained, previousHash))));

    /// <summary>The time as the form writes it: UTC, microseconds, a Z.</summary>
    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static void Separate(StringBuilder builder) => builder.Append(',');

    private static void Identifier(StringBuilder builder, Guid? id)
    {
        if (id is { } value)
        {
            Text(builder, value.ToString("D"));
        }
        else
        {
            builder.Append("null");
        }
    }

    private static void Text(StringBuilder builder, string? value)
    {
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

    private static void Details(StringBuilder builder, string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            builder.Append("null");
            return;
        }

        using var document = JsonDocument.Parse(details);
        Value(builder, document.RootElement);
    }

    private static void Value(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        Separate(builder);
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
                        Separate(builder);
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
    private static string Number(string raw)
    {
        var value = decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        var text = value.ToString("0.############################", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }
}

/// <summary>Where a tenant's chain stands (TF-061): its last sealed position and that event's hash.</summary>
public sealed record ChainHead(long Position, string Hash, DateTimeOffset SealedAt);

/// <summary>
/// What the unit tells the control plane after a seal (ADR-0050): the position and hash it reached, and, after a
/// retention pass, how far the oldest end was pruned. The control plane keeps every anchor in its own database.
/// </summary>
public sealed record AuditAnchor(string Kind, long Position, string Hash, DateTimeOffset At);

public static class AuditAnchorKinds
{
    /// <summary>The chain reached this position with this hash.</summary>
    public const string Seal = "seal";

    /// <summary>Everything up to this position was removed after its retention; the remaining chain starts from this hash.</summary>
    public const string Pruned = "pruned";
}
