using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Iron.Kernel.Data.Encryption;

/// <summary>
/// A field's value encrypted with the tenant's data key (TF-074 AC1, ADR-0054). A property of this type is stored as the
/// key service's ciphertext with its key version, never as the value, so a database dump shows nothing readable; the
/// re-encryption job moves it to a new version without the application seeing the value. Made and opened only through
/// <see cref="IFieldEncryption"/>. Its history records only that it changed.
/// </summary>
public sealed record EncryptedValue
{
    public EncryptedValue(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);
        if (KeyText.VersionOf(ciphertext) is null)
        {
            throw new ArgumentException("An encrypted value is the key service's ciphertext, with its version.", nameof(ciphertext));
        }

        Ciphertext = ciphertext;
    }

    public string Ciphertext { get; }

    /// <summary>The version of the data key that encrypted it.</summary>
    public int KeyVersion => KeyText.VersionOf(Ciphertext)!.Value;

    /// <summary>Never the ciphertext, so an encrypted value written into a message stays out of it.</summary>
    public override string ToString() => "[encrypted]";
}

/// <summary>
/// The keyed hash (HMAC-SHA256, the tenant's index key) of an encrypted field's normalised value (TF-074 AC2, ADR-0054),
/// kept beside it as <c>&lt;Property&gt;Hash</c>. A search by exact value hashes the input with every live version of the
/// key and compares; the hash reveals nothing without the key, and no other kind of search works on the field.
/// </summary>
public sealed record SearchHash
{
    public SearchHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (KeyText.VersionOf(value) is null)
        {
            throw new ArgumentException("A search hash is the key service's hash, with its version.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    /// <summary>The version of the index key that made it.</summary>
    public int KeyVersion => KeyText.VersionOf(Value)!.Value;

    public override string ToString() => "[hash]";
}

/// <summary>
/// Marks an <see cref="EncryptedValue"/> property found by its exact value: the entity keeps its keyed hash in a
/// <see cref="SearchHash"/> property of the same name followed by <c>Hash</c>, which the model check requires.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SearchableAttribute : Attribute
{
    /// <summary>The name of the hash property of an encrypted property.</summary>
    public static string HashPropertyOf(string propertyName) => propertyName + "Hash";
}

/// <summary>A searchable value as it is stored: the ciphertext and its keyed hash, set together.</summary>
public sealed record SearchableValue(EncryptedValue Value, SearchHash Hash);

/// <summary>
/// What an encrypted field's value is hashed as (ADR-0054): compatibility-composed Unicode, without white space, hyphens,
/// dots or slashes, in capitals. So <c>12-345 678</c> and <c>12345678</c> find each other, which is what people mean by the
/// same tax identifier or bank account; a part of a value finds nothing.
/// </summary>
public static class SearchNormalisation
{
    public static string Normalise(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var composed = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(composed.Length);
        foreach (var character in composed)
        {
            if (!char.IsWhiteSpace(character) && character is not ('-' or '.' or '/') && CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.DashPunctuation)
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }
}

/// <summary>The column of an encrypted value: its ciphertext as text.</summary>
public sealed class EncryptedValueConverter() : ValueConverter<EncryptedValue, string>(value => value.Ciphertext, text => new EncryptedValue(text));

/// <summary>The column of a search hash: the hash as text.</summary>
public sealed class SearchHashConverter() : ValueConverter<SearchHash, string>(hash => hash.Value, text => new SearchHash(text));
