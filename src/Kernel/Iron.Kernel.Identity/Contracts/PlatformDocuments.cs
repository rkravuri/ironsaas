using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iron.Kernel.Identity.Contracts;

/// <summary>Where a document a person must accept comes from (TF-028, ADR-0049): the platform's, or the tenant's own.</summary>
public static class DocumentSources
{
    public const string Platform = "platform";
    public const string Tenant = "tenant";
}

/// <summary>The platform's documents every person of every tenant accepts (TF-028): the terms of service and the privacy notice.</summary>
public static class PlatformDocumentKeys
{
    public const string TermsOfService = "terms_of_service";
    public const string PrivacyNotice = "privacy_notice";

    public static IReadOnlyList<string> All => [TermsOfService, PrivacyNotice];
}

/// <summary>One version of a document, as it is read and accepted.</summary>
public sealed record PublishedDocument(string Key, int Version, string Title, string Body, DateTimeOffset PublishedAt);

/// <summary>
/// The platform's documents in the store (ADR-0049): the control plane writes the current version of each to
/// <c>platform/documents</c>, which every unit's token may read; a unit keeps what it read for at most a minute.
/// </summary>
public static class PlatformDocumentsStore
{
    public const string Path = "platform/documents";

    public const string DocumentKey = "document";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ToJson(IReadOnlyList<PublishedDocument> documents) => JsonSerializer.Serialize(documents, Json);

    public static IReadOnlyList<PublishedDocument> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<PublishedDocument>>(json, Json) ?? [];
}
