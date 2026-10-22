using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Security.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>The platform's documents as a unit reads them (ADR-0049).</summary>
public interface IPlatformDocuments
{
    Task<IReadOnlyList<PublishedDocument>> CurrentAsync(CancellationToken cancellationToken);
}

/// <summary>No store: the platform has no documents for this unit. A host run from source, and the tests that bring none.</summary>
public sealed class NoPlatformDocuments : IPlatformDocuments
{
    public Task<IReadOnlyList<PublishedDocument>> CurrentAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PublishedDocument>>([]);
}

/// <summary>
/// The store's copy of the platform's documents (<c>platform/documents</c>), which the control plane writes on each new
/// version; read at most once a minute per process, so a new version applies everywhere within a minute. A store that
/// cannot be read keeps the last answer, and a unit that never read one asks nobody to accept anything of the platform's.
/// </summary>
public sealed class StorePlatformDocuments(ISecretStore store, TimeProvider time, ILogger<StorePlatformDocuments> logger) : IPlatformDocuments
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private (IReadOnlyList<PublishedDocument> Documents, DateTimeOffset ReadAt)? _cached;

    public async Task<IReadOnlyList<PublishedDocument>> CurrentAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_cached is { } cached && now - cached.ReadAt < CacheFor)
            {
                return cached.Documents;
            }
        }

        IReadOnlyList<PublishedDocument> documents;
        try
        {
            var values = await store.ReadAsync(PlatformDocumentsStore.Path, cancellationToken).ConfigureAwait(false);
            documents = PlatformDocumentsStore.Parse(values?.GetValueOrDefault(PlatformDocumentsStore.DocumentKey));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(exception, "The platform's documents could not be read from the store; the last ones read stand.");
            lock (_gate)
            {
                return _cached?.Documents ?? [];
            }
        }

        lock (_gate)
        {
            _cached = (documents, now);
        }

        return documents;
    }
}

/// <summary>A version of a document a person must accept, and where it comes from.</summary>
public sealed record RequiredDocument(string Source, string Key, int Version, string Title, string Body, DateTimeOffset PublishedAt);

/// <summary>A document version a person says they accept.</summary>
public sealed record DocumentChoice(string Source, string Key, int Version);

/// <summary>
/// The tenant's documents' current versions, kept for at most a minute per process like the platform's; a new version
/// published in this process applies here at once.
/// </summary>
public sealed class TenantDocumentVersions(TimeProvider time)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private (IReadOnlyList<TenantDocument> Documents, DateTimeOffset ReadAt)? _cached;

    public async Task<IReadOnlyList<TenantDocument>> CurrentAsync(IdentityDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_cached is { } cached && now - cached.ReadAt < CacheFor)
            {
                return cached.Documents;
            }
        }

        var all = await context.TenantDocuments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TenantDocument> current = [.. all.GroupBy(d => d.Key).Select(g => g.MaxBy(d => d.Number)!).Where(d => !d.Retired).OrderBy(d => d.Key, StringComparer.Ordinal)];
        lock (_gate)
        {
            _cached = (current, now);
        }

        return current;
    }

    public void Changed()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }
}

/// <summary>
/// Terms acceptance (TF-028, ADR-0049): the platform's documents and the tenant's own, each in versions. A person must
/// accept every current version before anything else, at their next request, as a required step; declining keeps them out
/// until they accept. Each acceptance stores the version, the time and the person. A service account never accepts: it
/// signs in with tokens, which a required step does not stop. Administrators publish the tenant's versions and read who
/// accepted which.
/// </summary>
public sealed class TermsService(
    IdentityDbContext context,
    IPlatformDocuments platform,
    TenantDocumentVersions tenantVersions,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    TimeProvider time)
{
    /// <summary>Every current version: the platform's first, then the tenant's by key.</summary>
    public async Task<IReadOnlyList<RequiredDocument>> CurrentAsync(CancellationToken cancellationToken)
    {
        var platformDocuments = await platform.CurrentAsync(cancellationToken).ConfigureAwait(false);
        var tenantDocuments = await tenantVersions.CurrentAsync(context, cancellationToken).ConfigureAwait(false);
        return
        [
            .. platformDocuments.Select(d => new RequiredDocument(DocumentSources.Platform, d.Key, d.Version, d.Title, d.Body, d.PublishedAt)),
            .. tenantDocuments.Select(d => new RequiredDocument(DocumentSources.Tenant, d.Key, d.Number, d.Title, d.Body, d.PublishedAt)),
        ];
    }

    /// <summary>The current versions the person has not accepted; none at all costs no query when no document exists.</summary>
    public async Task<IReadOnlyList<RequiredDocument>> PendingAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var current = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current.Count == 0)
        {
            return [];
        }

        var accepted = await context.DocumentAcceptances.AsNoTracking()
            .Where(a => a.PrincipalId == principalId)
            .Select(a => new { a.Source, a.DocumentKey, a.Version })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. current.Where(d => !accepted.Any(a => a.Source == d.Source && a.DocumentKey == d.Key && a.Version == d.Version))];
    }

    /// <summary>
    /// The person accepts the versions they read (TF-028 AC2): every pending one must be among them, at the version that is
    /// current now, or nothing is recorded and the person reads the newer version first.
    /// </summary>
    public async Task<Result<int>> AcceptAsync(Principal person, IReadOnlyList<DocumentChoice> choices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(choices);
        var pending = await PendingAsync(person.Id, cancellationToken).ConfigureAwait(false);
        var missing = pending.Where(d => !choices.Any(c => c.Source == d.Source && c.Key == d.Key && c.Version == d.Version)).ToList();
        if (missing.Count > 0)
        {
            return Error.Conflict(IdentityErrors.TermsVersionChanged, $"Accept every document you must accept, at its current version: {string.Join(", ", missing.Select(d => $"{d.Title} (version {d.Version})"))}. A newer version may have been published while you read; read it first.");
        }

        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        foreach (var document in pending)
        {
            context.DocumentAcceptances.Add(new DocumentAcceptance(person.Id, document.Source, document.Key, document.Version, now));
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var document in pending)
        {
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.TermsAccepted, AuditOutcome.Success, person.Id, person.LoginId, Details(document), "document", null), cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return pending.Count;
    }

    /// <summary>The person declines (TF-028 AC4): audited with the versions they declined; the caller ends the session.</summary>
    public async Task DeclinedAsync(Principal person, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(person);
        foreach (var document in await PendingAsync(person.Id, cancellationToken).ConfigureAwait(false))
        {
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.TermsDeclined, AuditOutcome.Denied, person.Id, person.LoginId, Details(document), "document", null), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An administrator publishes a tenant document's next version, or its first under a new key (ADR-0049): everyone accepts
    /// it at their next request, here at once and in every other process within a minute.
    /// </summary>
    public async Task<Result<TenantDocument>> PublishAsync(string key, string title, string body, CancellationToken cancellationToken)
    {
        var normalised = key?.Trim().ToLowerInvariant();
        if (!TenantDocument.IsKey(normalised))
        {
            return Error.Validation(IdentityErrors.DocumentKeyInvalid, "A document's key is lowercase letters, digits and hyphens, starting with a letter, 64 at most.", new FieldError("key", "invalid", "Use lowercase letters, digits and hyphens."));
        }

        var latest = await context.TenantDocuments.Where(d => d.Key == normalised).MaxAsync(d => (int?)d.Number, cancellationToken).ConfigureAwait(false) ?? 0;
        var document = new TenantDocument(normalised!, latest + 1, title, body, time.GetUtcNow());
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.TenantDocuments.Add(document);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["key"] = document.Key, ["version"] = document.Number.ToString(CultureInfo.InvariantCulture), ["title"] = document.Title };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.DocumentPublished, AuditOutcome.Success, actor.Id, document.Key, details, "document", document.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        tenantVersions.Changed();
        return document;
    }

    /// <summary>
    /// An administrator retires a tenant document (ADR-0049): its current version is asked of nobody any more, here at once
    /// and in every other process within a minute; the acceptances stay. Publishing a version later asks it again.
    /// </summary>
    public async Task<Result> RetireAsync(string key, CancellationToken cancellationToken)
    {
        var latest = await context.TenantDocuments.Where(d => d.Key == key).OrderByDescending(d => d.Number).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null || latest.Retired)
        {
            return Error.NotFound(IdentityErrors.DocumentNotFound, $"There is no document '{key}' that people are asked to accept.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        latest.Retire();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["key"] = latest.Key, ["version"] = latest.Number.ToString(CultureInfo.InvariantCulture) };
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.DocumentRetired, AuditOutcome.Success, actor.Id, latest.Key, details, "document", latest.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        tenantVersions.Changed();
        return Result.Success();
    }

    private static Dictionary<string, string> Details(RequiredDocument document) => new(StringComparer.Ordinal)
    {
        ["source"] = document.Source,
        ["key"] = document.Key,
        ["version"] = document.Version.ToString(CultureInfo.InvariantCulture),
    };
}
