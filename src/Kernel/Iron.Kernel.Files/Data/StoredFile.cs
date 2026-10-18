using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Files.Data;

/// <summary>
/// The metadata of one stored file (TF-160): name, type, size, SHA-256, the random storage key, and the record it hangs
/// on. The bytes live in object storage; the row is the truth about them. Deleting marks the row (retention, TF-165).
/// </summary>
public sealed class StoredFile : AuditedEntity, ISoftDeletable
{
    public const int NameMaxLength = 255;
    public const int ContentTypeMaxLength = 127;
    public const int StorageKeyMaxLength = 128;

    private StoredFile()
    {
        Name = string.Empty;
        ContentType = string.Empty;
        Sha256 = string.Empty;
        StorageKey = string.Empty;
    }

    public StoredFile(string name, string contentType, long size, string sha256, string storageKey, string? recordType, Guid? recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        Name = name.Length > NameMaxLength ? name[..NameMaxLength] : name;
        ContentType = contentType;
        Size = size;
        Sha256 = sha256;
        StorageKey = storageKey;
        RecordType = recordType;
        RecordId = recordId;
    }

    /// <summary>The name the uploader gave, for downloads. Never used as a storage key.</summary>
    public string Name { get; private set; }

    public string ContentType { get; private set; }

    public long Size { get; private set; }

    /// <summary>Lowercase hex SHA-256 of the content, checked again on download (TF-160 AC1).</summary>
    public string Sha256 { get; private set; }

    public string StorageKey { get; private set; }

    public string? RecordType { get; private set; }

    public Guid? RecordId { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }
}
