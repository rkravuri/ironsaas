using System.Text;

namespace Iron.Kernel.Data.Encryption;

/// <summary>
/// How a module encrypts, opens and finds its encrypted fields (TF-074, ADR-0054), through the tenant's key service.
/// A value is plain in memory only between opening it and showing it; the keys never are.
/// </summary>
public interface IFieldEncryption
{
    Task<IReadOnlyList<EncryptedValue>> EncryptAsync(IReadOnlyList<byte[]> values, CancellationToken cancellationToken);

    Task<IReadOnlyList<byte[]>> DecryptAsync(IReadOnlyList<EncryptedValue> values, CancellationToken cancellationToken);

    /// <summary>The value encrypted, and the keyed hash of its normalised form, for a <see cref="SearchableAttribute"/> field.</summary>
    Task<SearchableValue> SealSearchableAsync(string value, CancellationToken cancellationToken);

    /// <summary>
    /// The keyed hashes of a value to search for, one per live version of the index key: while a rotation's job recomputes
    /// the hashes, a value is found under the old version or the new one. A query compares the hash column with any of them.
    /// </summary>
    Task<IReadOnlyList<SearchHash>> SearchHashesAsync(string value, CancellationToken cancellationToken);
}

public static class FieldEncryptionExtensions
{
    public static async Task<EncryptedValue> EncryptAsync(this IFieldEncryption encryption, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(value);
        return (await encryption.EncryptAsync([Encoding.UTF8.GetBytes(value)], cancellationToken).ConfigureAwait(false))[0];
    }

    public static async Task<EncryptedValue> EncryptAsync(this IFieldEncryption encryption, byte[] value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        return (await encryption.EncryptAsync([value], cancellationToken).ConfigureAwait(false))[0];
    }

    public static async Task<string> DecryptAsync(this IFieldEncryption encryption, EncryptedValue value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        return Encoding.UTF8.GetString((await encryption.DecryptAsync([value], cancellationToken).ConfigureAwait(false))[0]);
    }

    public static async Task<byte[]> DecryptBytesAsync(this IFieldEncryption encryption, EncryptedValue value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        return (await encryption.DecryptAsync([value], cancellationToken).ConfigureAwait(false))[0];
    }

    public static async Task<IReadOnlyList<string>> DecryptAsync(this IFieldEncryption encryption, IReadOnlyList<EncryptedValue> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        return [.. (await encryption.DecryptAsync(values, cancellationToken).ConfigureAwait(false)).Select(Encoding.UTF8.GetString)];
    }
}

/// <summary>The fields' encryption on the tenant's data and index keys.</summary>
public sealed class FieldEncryption(IKeyService keys) : IFieldEncryption
{
    public async Task<IReadOnlyList<EncryptedValue>> EncryptAsync(IReadOnlyList<byte[]> values, CancellationToken cancellationToken) =>
        [.. (await keys.EncryptAsync(KeyPurpose.Data, values, cancellationToken).ConfigureAwait(false)).Select(c => new EncryptedValue(c))];

    public Task<IReadOnlyList<byte[]>> DecryptAsync(IReadOnlyList<EncryptedValue> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        return keys.DecryptAsync(KeyPurpose.Data, [.. values.Select(v => v.Ciphertext)], cancellationToken);
    }

    public async Task<SearchableValue> SealSearchableAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        var encrypted = await keys.EncryptAsync(KeyPurpose.Data, Encoding.UTF8.GetBytes(value), cancellationToken).ConfigureAwait(false);
        var hash = await keys.HashAsync(KeyPurpose.Index, Encoding.UTF8.GetBytes(SearchNormalisation.Normalise(value)), null, cancellationToken).ConfigureAwait(false);
        return new SearchableValue(new EncryptedValue(encrypted), new SearchHash(hash));
    }

    public async Task<IReadOnlyList<SearchHash>> SearchHashesAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        var input = Encoding.UTF8.GetBytes(SearchNormalisation.Normalise(value));
        var versions = await keys.VersionsAsync(KeyPurpose.Index, cancellationToken).ConfigureAwait(false);
        var hashes = new List<SearchHash>();
        foreach (var version in versions.Live)
        {
            hashes.Add(new SearchHash(await keys.HashAsync(KeyPurpose.Index, input, version, cancellationToken).ConfigureAwait(false)));
        }

        return hashes;
    }
}
