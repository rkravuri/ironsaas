using System.Text.RegularExpressions;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>
/// One version of a document the tenant's people must accept (TF-028, ADR-0049): written once, never changed; a change is
/// a new version, which everyone accepts again at their next request. The key names the document across its versions.
/// </summary>
public sealed partial class TenantDocument : AuditedEntity, ITenantWide
{
    public const int KeyMaxLength = 64;
    public const int TitleMaxLength = 200;
    public const int BodyMaxLength = 100_000;

    private TenantDocument()
    {
        Key = string.Empty;
        Title = string.Empty;
        Body = string.Empty;
    }

    public TenantDocument(string key, int version, string title, string body, DateTimeOffset publishedAt)
        : this()
    {
        if (!IsKey(key))
        {
            throw new ArgumentException("A document's key is lowercase letters, digits and hyphens, starting with a letter.", nameof(key));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        Key = key;
        Number = version;
        Title = title.Trim();
        Body = body;
        PublishedAt = publishedAt;
    }

    public string Key { get; private set; }

    /// <summary>The version's number, from 1; the row's own concurrency version is another thing.</summary>
    public int Number { get; private set; }

    public string Title { get; private set; }

    public string Body { get; private set; }

    public DateTimeOffset PublishedAt { get; private set; }

    public static bool IsKey(string? key) => key is { Length: > 0 and <= KeyMaxLength } && KeyPattern().IsMatch(key);

    [GeneratedRegex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>
/// A person accepted one version of a document (TF-028 AC2): which, when, who. Append-only; administrators list and
/// export them. A new version needs a new acceptance.
/// </summary>
public sealed class DocumentAcceptance : AppendOnlyEntity, ITenantWide
{
    public const int SourceMaxLength = 20;

    private DocumentAcceptance()
    {
        Source = string.Empty;
        DocumentKey = string.Empty;
    }

    public DocumentAcceptance(Guid principalId, string source, string documentKey, int version, DateTimeOffset acceptedAt)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        PrincipalId = principalId;
        Source = source;
        DocumentKey = documentKey;
        Version = version;
        AcceptedAt = acceptedAt;
    }

    public Guid PrincipalId { get; private set; }

    /// <summary><c>platform</c> or <c>tenant</c>.</summary>
    public string Source { get; private set; }

    public string DocumentKey { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset AcceptedAt { get; private set; }
}
