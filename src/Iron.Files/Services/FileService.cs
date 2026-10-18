using Iron.Audit.Contracts;
using Iron.Core.Identifiers;
using Iron.Core.Results;
using Iron.Data.Transactions;
using Iron.Files.Contracts;
using Iron.Files.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Files.Services;

/// <summary>What a download hands to the endpoint: the metadata and a stream that hashes as it is read.</summary>
public sealed record FileDownload(StoredFile File, HashingStream Content);

/// <summary>
/// Upload, download and delete (TF-160, thin): random storage keys, the hash recorded on upload and checked on download,
/// the row and the audit event in one transaction, soft delete. Type rules and scanning (TF-161, TF-078) come later.
/// </summary>
public sealed class FileService(
    FilesDbContext context,
    IObjectStorage storage,
    StorageOptions options,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    IAuditWriter audit,
    ILogger<FileService> logger)
{
    public async Task<Result<StoredFile>> UploadAsync(string name, string contentType, long declaredLength, Stream content, string? recordType, Guid? recordId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (declaredLength > options.MaxUploadBytes)
        {
            return Error.Validation(FilesErrors.FileTooLarge, $"The file is larger than {options.MaxUploadMegabytes} MB.");
        }

        var key = NewKey();
        await using var hashing = new HashingStream(new LimitedStream(content, options.MaxUploadBytes));
        try
        {
            await storage.PutAsync(key, hashing, string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is LimitExceededException || exception.InnerException is LimitExceededException)
        {
            await storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            return Error.Validation(FilesErrors.FileTooLarge, $"The file is larger than {options.MaxUploadMegabytes} MB.");
        }

        if (hashing.BytesRead == 0)
        {
            await storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            return Error.Validation(FilesErrors.FileEmpty, "The file is empty.");
        }

        var file = new StoredFile(string.IsNullOrWhiteSpace(name) ? "file" : Path.GetFileName(name), contentType, hashing.BytesRead, hashing.HexHash(), key, recordType, recordId);
        try
        {
            await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            context.Files.Add(file);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEvent(FilesAuditEvents.Uploaded, AuditOutcome.Success, file.CreatedBy, file.Name, Details(file), "file", file.Id), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await storage.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return file;
    }

    public async Task<StoredFile?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await context.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken).ConfigureAwait(false);

    public async Task<Result<FileDownload>> OpenAsync(Guid id, CancellationToken cancellationToken)
    {
        var file = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return Error.NotFound(FilesErrors.FileNotFound, "There is no such file.");
        }

        var stored = await storage.GetAsync(file.StorageKey, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            logger.LogError("File {FileId} has no content at storage key {StorageKey}.", file.Id, file.StorageKey);
            return Error.Unexpected(FilesErrors.FileMissing, "The file's content is missing from storage.");
        }

        return new FileDownload(file, new HashingStream(stored.Content));
    }

    /// <summary>After a download streamed: checks the hash against the row and audits the download, or the integrity failure (TF-160 AC1).</summary>
    public async Task<bool> CompleteDownloadAsync(FileDownload download, Guid principalId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(download);
        var actual = download.Content.HexHash();
        if (string.Equals(actual, download.File.Sha256, StringComparison.Ordinal))
        {
            await audit.WriteAsync(new AuditEvent(FilesAuditEvents.Downloaded, AuditOutcome.Success, principalId, download.File.Name, Details(download.File), "file", download.File.Id), cancellationToken).ConfigureAwait(false);
            return true;
        }

        logger.LogError("File {FileId} failed its integrity check: stored {Expected}, read {Actual}.", download.File.Id, download.File.Sha256, actual);
        await audit.WriteAsync(new AuditEvent(FilesAuditEvents.IntegrityFailed, AuditOutcome.Failure, principalId, download.File.Name, new Dictionary<string, string>(StringComparer.Ordinal) { ["expected_sha256"] = download.File.Sha256, ["actual_sha256"] = actual }, "file", download.File.Id), cancellationToken).ConfigureAwait(false);
        return false;
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var file = await context.Files.SingleOrDefaultAsync(f => f.Id == id, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return Error.NotFound(FilesErrors.FileNotFound, "There is no such file.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.Files.Remove(file); // the interceptor turns this into a soft delete
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(FilesAuditEvents.Deleted, AuditOutcome.Success, file.DeletedBy ?? Guid.Empty, file.Name, Details(file), "file", file.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Random, dated keys: <c>2026/10/0192fd3e-...</c>. Never derived from the file name.</summary>
    private string NewKey()
    {
        var now = time.GetUtcNow();
        return $"{now:yyyy}/{now:MM}/{Ids.New(time):D}";
    }

    private static Dictionary<string, string> Details(StoredFile file) => new(StringComparer.Ordinal)
    {
        ["content_type"] = file.ContentType,
        ["size"] = file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["sha256"] = file.Sha256,
    };
}

/// <summary>Thrown by <see cref="LimitedStream"/> when a stream runs past the allowed length.</summary>
public sealed class LimitExceededException : Exception
{
    public LimitExceededException()
    {
    }

    public LimitExceededException(string message)
        : base(message)
    {
    }

    public LimitExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reads through to another stream and stops with an exception once more than the limit has passed.</summary>
public sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Account(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Account(int read)
    {
        _read += read;
        if (_read > limit)
        {
            throw new LimitExceededException($"The content is longer than {limit} bytes.");
        }
    }
}
