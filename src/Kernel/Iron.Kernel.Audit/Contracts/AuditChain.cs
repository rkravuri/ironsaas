using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iron.Kernel.History;

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
    string? Details,
    string? Changes = null);

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
/// Version 2 (ADR-0051) is version 1 with the label <c>iron-audit-v2</c> and the field changes of a record's save
/// (TF-062) between the details and the previous hash: null, or an array of objects with the keys <c>field</c>,
/// <c>new</c>, <c>old</c> and <c>treatment</c>, written as the details are. Events sealed with version 1 keep it.
/// </summary>
public static class AuditCanonicalForm
{
    /// <summary>The version new seals use, kept with every sealed event, so every earlier form stays verifiable.</summary>
    public const short Version = 2;

    /// <summary>The first word of the array, which names the form and its version.</summary>
    public const string Label = "iron-audit-v2";

    /// <summary>The forms this build can recompute.</summary>
    public static bool Knows(short version) => version is 1 or 2;

    /// <summary>The first word of a version's array.</summary>
    public static string LabelOf(short version) => $"iron-audit-v{version.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The previous hash of position 1.</summary>
    public static readonly string Genesis = new('0', 64);

    public static string Write(ChainedEvent chained, string previousHash) => Write(chained, previousHash, Version);

    public static string Write(ChainedEvent chained, string previousHash, short version)
    {
        ArgumentNullException.ThrowIfNull(chained);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousHash);
        if (!Knows(version))
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "This build knows the canonical forms 1 and 2.");
        }

        var builder = new StringBuilder(512);
        builder.Append('[');
        Text(builder, LabelOf(version));
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
        if (version >= 2)
        {
            Details(builder, chained.Changes);
            Separate(builder);
        }

        Text(builder, previousHash);
        builder.Append(']');
        return builder.ToString();
    }

    public static string Hash(ChainedEvent chained, string previousHash) => Hash(chained, previousHash, Version);

    public static string Hash(ChainedEvent chained, string previousHash, short version) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Write(chained, previousHash, version))));

    /// <summary>The time as the form writes it: UTC, microseconds, a Z.</summary>
    public static string Time(DateTimeOffset at) => CanonicalJson.Time(at);

    // The rules of text, identifiers and JSON are the kernel's canonical JSON, which the snapshot form shares (ADR-0051).
    private static void Separate(StringBuilder builder) => builder.Append(',');

    private static void Identifier(StringBuilder builder, Guid? id) => CanonicalJson.Identifier(builder, id);

    private static void Text(StringBuilder builder, string? value) => CanonicalJson.Text(builder, value);

    private static void Details(StringBuilder builder, string? details) => CanonicalJson.Json(builder, details);
}

/// <summary>Where a tenant's chain stands (TF-061): its last sealed position and that event's hash.</summary>
public sealed record ChainHead(long Position, string Hash, DateTimeOffset SealedAt);

/// <summary>
/// What the unit tells the control plane after a seal (ADR-0050): the position and hash it reached, and, after a
/// retention pass, how far the oldest end was pruned. The control plane keeps every anchor in its own database, in the
/// segment that covers its position (<see cref="AuditChainSegments"/>).
/// </summary>
public sealed record AuditAnchor(string Kind, long Position, string Hash, DateTimeOffset At, int Segment = AuditChainSegments.First);

public static class AuditAnchorKinds
{
    /// <summary>The chain reached this position with this hash.</summary>
    public const string Seal = "seal";

    /// <summary>Everything up to this position was removed after its retention; the remaining chain starts from this hash.</summary>
    public const string Pruned = "pruned";
}
