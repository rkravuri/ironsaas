using System.Security.Cryptography;

namespace Iron.Kernel.Files.Services;

/// <summary>Reads through to another stream and keeps a SHA-256 of everything that passed, so uploads and downloads hash while they stream.</summary>
public sealed class HashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _length;
    private string? _hexHash;
    private bool _disposed;

    public long BytesRead => _length;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    /// <summary>The lowercase hex SHA-256 of everything read. Final once called or once the stream is disposed; both are fine, in either order.</summary>
    public string HexHash() => _hexHash ??= Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Account(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Account(buffer[..read]);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Account(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(buffer.Span[..read]);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            HexHash(); // finish the hash while the hasher is still alive; the caller reads it after the copy
            _hash.Dispose();
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Account(ReadOnlySpan<byte> bytes)
    {
        _hash.AppendData(bytes);
        _length += bytes.Length;
    }
}
