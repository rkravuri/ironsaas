using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Iron.Kernel.Audit.Contracts;

/// <summary>
/// One archived batch of a tenant's audit trail (TF-064 AC2, ADR-0050): consecutive positions, kept whole with their chain
/// columns, as JSON lines in the tenant's container. The unit writes it, its search and the fleet's verification read it,
/// so the chain verifies across the database and the archive alike. Each line holds one event: its content as the chain
/// hashes it, its previous hash, its hash, the version and when it was sealed.
/// </summary>
public static class AuditArchiveFormat
{
    public const string ContentType = "application/x-ndjson";

    /// <summary>Where a batch lives in the tenant's container.</summary>
    public static string KeyOf(long firstPosition, long lastPosition) =>
        string.Create(CultureInfo.InvariantCulture, $"audit/archive/{firstPosition:D12}-{lastPosition:D12}.jsonl");

    public static byte[] Write(IEnumerable<SealedEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var builder = new StringBuilder();
        foreach (var sealedEvent in events.OrderBy(e => e.Position))
        {
            var content = sealedEvent.Content;
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("id", content.Id);
                writer.WriteNumber("position", content.Position);
                writer.WriteString("created_at", AuditCanonicalForm.Time(content.CreatedAt));
                writer.WriteString("created_by", content.CreatedBy);
                writer.WriteString("event_type", content.EventType);
                writer.WriteString("outcome", content.Outcome);
                writer.WriteString("actor_id", content.ActorId);
                WriteGuid(writer, "on_behalf_of_id", content.OnBehalfOfId);
                WriteGuid(writer, "impersonator_id", content.ImpersonatorId);
                writer.WriteString("source", content.Source);
                writer.WriteString("severity", content.Severity);
                writer.WriteString("target", content.Target);
                writer.WriteString("record_type", content.RecordType);
                WriteGuid(writer, "record_id", content.RecordId);
                writer.WriteString("reason", content.Reason);
                writer.WriteString("correlation_id", content.CorrelationId);
                writer.WritePropertyName("details");
                if (string.IsNullOrWhiteSpace(content.Details))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    using var details = JsonDocument.Parse(content.Details);
                    details.RootElement.WriteTo(writer);
                }

                writer.WritePropertyName("changes");
                if (string.IsNullOrWhiteSpace(content.Changes))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    using var changes = JsonDocument.Parse(content.Changes);
                    changes.RootElement.WriteTo(writer);
                }

                writer.WriteString("previous_hash", sealedEvent.PreviousHash);
                writer.WriteString("hash", sealedEvent.Hash);
                writer.WriteNumber("hash_version", sealedEvent.HashVersion);
                writer.WriteString("sealed_at", AuditCanonicalForm.Time(sealedEvent.SealedAt));
                writer.WriteEndObject();
            }

            builder.Append(Encoding.UTF8.GetString(buffer.ToArray())).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static IReadOnlyList<SealedEvent> Read(ReadOnlySpan<byte> batch)
    {
        var events = new List<SealedEvent>();
        foreach (var line in Encoding.UTF8.GetString(batch).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var details = root.GetProperty("details");

            // A batch archived before version 2 has no changes: version 1 never hashed them.
            var changes = root.TryGetProperty("changes", out var found) ? found : default;
            events.Add(new SealedEvent(
                new ChainedEvent(
                    root.GetProperty("id").GetGuid(),
                    root.GetProperty("position").GetInt64(),
                    Time(root.GetProperty("created_at")),
                    root.GetProperty("created_by").GetGuid(),
                    root.GetProperty("event_type").GetString()!,
                    root.GetProperty("outcome").GetString()!,
                    root.GetProperty("actor_id").GetGuid(),
                    OptionalGuid(root.GetProperty("on_behalf_of_id")),
                    OptionalGuid(root.GetProperty("impersonator_id")),
                    root.GetProperty("source").GetString()!,
                    root.GetProperty("severity").GetString()!,
                    root.GetProperty("target").GetString(),
                    root.GetProperty("record_type").GetString(),
                    OptionalGuid(root.GetProperty("record_id")),
                    root.GetProperty("reason").GetString(),
                    root.GetProperty("correlation_id").GetString(),
                    details.ValueKind == JsonValueKind.Null ? null : details.GetRawText(),
                    changes.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : changes.GetRawText()),
                root.GetProperty("previous_hash").GetString()!,
                root.GetProperty("hash").GetString()!,
                root.GetProperty("hash_version").GetInt16(),
                Time(root.GetProperty("sealed_at"))));
        }

        return events;
    }

    /// <summary>The batch's SHA-256, which its index row keeps: a batch changed in the container no longer matches.</summary>
    public static string Sha256(ReadOnlySpan<byte> batch) => Convert.ToHexStringLower(SHA256.HashData(batch));

    private static void WriteGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } id)
        {
            writer.WriteString(name, id);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static Guid? OptionalGuid(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetGuid();

    private static DateTimeOffset Time(JsonElement element) =>
        DateTimeOffset.ParseExact(element.GetString()!, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
