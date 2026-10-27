using System.Globalization;
using System.Text;
using Iron.Kernel.Files.Data;

namespace Iron.Kernel.Files.Services;

/// <summary>
/// The name a file is shown by (TF-071 AC4, ADR-0053). The uploader's name is display text only: the bytes are stored
/// under a random key, and the name becomes its last path part, without control or formatting characters (such as a
/// right-to-left override that hides an extension), without characters file systems refuse, without leading dots or a
/// device's name, and at most <see cref="StoredFile.NameMaxLength"/> characters with its extension kept.
/// </summary>
public static class FileNames
{
    /// <summary>The name of a file whose own name has nothing left.</summary>
    public const string Fallback = "file";

    /// <summary>An extension longer than this is not kept apart when the name is shortened.</summary>
    private const int ExtensionMaxLength = 20;

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Normalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fallback;
        }

        // The last part of any path, whichever separator the client's system uses; a drive or a stream name goes too.
        var text = name.Normalize(NormalizationForm.FormC);
        var lastPart = text[(text.LastIndexOfAny(['/', '\\']) + 1)..];

        var builder = new StringBuilder(lastPart.Length);
        foreach (var rune in lastPart.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            var refused = rune.Value is '<' or '>' or ':' or '"' or '|' or '?' or '*';
            builder.Append(refused ? "_" : Rune.IsWhiteSpace(rune) ? " " : rune.ToString());
        }

        var cleaned = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (cleaned.Length == 0)
        {
            return Fallback;
        }

        var stem = cleaned.Split('.')[0];
        if (DeviceNames.Contains(stem))
        {
            cleaned = "_" + cleaned;
        }

        return Shorten(cleaned);
    }

    private static string Shorten(string name)
    {
        if (name.Length <= StoredFile.NameMaxLength)
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        var extension = dot > 0 && name.Length - dot <= ExtensionMaxLength + 1 ? name[dot..] : string.Empty;
        var stem = name[..(StoredFile.NameMaxLength - extension.Length)];

        // Never cut a character in two.
        if (char.IsHighSurrogate(stem[^1]))
        {
            stem = stem[..^1];
        }

        return stem.TrimEnd('.', ' ') + extension;
    }
}
