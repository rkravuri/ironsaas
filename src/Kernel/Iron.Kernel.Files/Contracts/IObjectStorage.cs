namespace Iron.Kernel.Files.Contracts;

/// <summary>A stored object as read back: the content stream, its type and length. Dispose it after reading.</summary>
public sealed record StoredObject(Stream Content, string ContentType, long Length) : IDisposable
{
    public void Dispose() => Content.Dispose();
}

/// <summary>
/// Object storage behind one interface (TF-160, ADR-0006): Azure Blob Storage, with Azurite on the laptop. One container
/// per tenant; keys are random and never carry user input. Versions, retention and signed links are Iron.Kernel.Files's job
/// on top of this.
/// </summary>
public interface IObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>The object, or null when the key does not exist.</summary>
    Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>True when something was deleted.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>Creates the tenant's container when it is missing. The fleet calls it when it creates a unit; the seeder calls it too.</summary>
    Task EnsureContainerAsync(CancellationToken cancellationToken);

    /// <summary>Null when the container is there, otherwise the reason the unit cannot store files.</summary>
    Task<string?> CheckAsync(CancellationToken cancellationToken);
}

public static class FilesPermissions
{
    public static readonly Permission FilesRead = new("files.files.read", "View file details and download files", Risk.Low);

    public static readonly Permission FilesUpload = new("files.files.upload", "Upload files", Risk.Medium);

    public static readonly Permission FilesDelete = new("files.files.delete", "Delete files", Risk.High);

    public static IReadOnlyList<Permission> All => [FilesRead, FilesUpload, FilesDelete];
}

public static class FilesErrors
{
    public const string FileNotFound = "files.file.not_found";
    public const string FileMissing = "files.file.missing";
    public const string FileTooLarge = "files.file.too_large";
    public const string FileEmpty = "files.file.empty";
    public const string ContentCorrupt = "files.file.corrupt";
}

public static class FilesAuditEvents
{
    public const string Uploaded = "files.file.uploaded";
    public const string Downloaded = "files.file.downloaded";
    public const string Deleted = "files.file.deleted";
    public const string IntegrityFailed = "files.file.integrity_failed";
}
