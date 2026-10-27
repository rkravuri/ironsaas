using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.Validators;
using Ganss.Xss;

namespace Iron.Kernel.Security.Text;

/// <summary>
/// Formatted text a person writes, such as a note or a document's terms (TF-071 AC2, ADR-0053). It holds HTML cleaned by
/// an allow-list as it is made, so a request's field of this type never carries a script into a record: the JSON
/// converter cleans what the client sent before the handler sees it. The shell renders it in one component.
/// </summary>
[JsonConverter(typeof(RichTextJsonConverter))]
public sealed class RichText : IEquatable<RichText>
{
    /// <summary>The longest text read before cleaning: more is refused unread, so cleaning cannot be made to cost much.</summary>
    public const int InputMaxLength = 256 * 1024;

    private RichText(string html) => Html = html;

    public static RichText Empty { get; } = new(string.Empty);

    /// <summary>The cleaned HTML.</summary>
    public string Html { get; }

    public int Length => Html.Length;

    /// <summary>Cleans <paramref name="html"/> by the allow-list: what is not on it goes, its text stays where it is safe.</summary>
    public static RichText From(string? html) =>
        string.IsNullOrWhiteSpace(html) ? Empty : new RichText(RichTextSanitizer.Clean(html));

    public bool Equals(RichText? other) => other is not null && string.Equals(Html, other.Html, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as RichText);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Html);

    public override string ToString() => Html;
}

/// <summary>
/// The allow-list (ADR-0053): paragraphs, line breaks, emphasis, lists, quotes, code, headings from the second level,
/// tables and links. Links keep only http, https and mailto addresses and open nothing by script. Everything else goes:
/// scripts, styles, frames, forms, images, event handlers, style attributes and every other scheme.
/// </summary>
public static class RichTextSanitizer
{
    public static IReadOnlySet<string> Tags { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "strong", "b", "em", "i", "u", "s", "sub", "sup", "ul", "ol", "li", "blockquote", "code", "pre",
        "h2", "h3", "h4", "hr", "table", "thead", "tbody", "tr", "th", "td", "a",
    };

    public static IReadOnlySet<string> Attributes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "href", "title", "colspan", "rowspan",
    };

    public static IReadOnlySet<string> Schemes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto",
    };

    /// <summary>Tags removed with everything inside them.</summary>
    private static readonly HashSet<string> DroppedWhole = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "template", "noscript", "iframe", "frame", "frameset", "object", "embed", "svg", "math",
        "form", "textarea", "select", "option", "button", "input", "title", "head", "link", "meta", "base",
    };

    // Built after the lists it reads; once configured it is safe to share between threads.
    private static readonly HtmlSanitizer Sanitizer = Build();

    public static string Clean(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return Sanitizer.Sanitize(html).Trim();
    }

    private static HtmlSanitizer Build()
    {
        var options = new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(Tags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(Attributes, StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(Schemes, StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(["href"], StringComparer.OrdinalIgnoreCase),
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowedAtRules = new HashSet<AngleSharp.Css.Dom.CssRuleType>(),
            AllowedCssClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };

        // A tag off the list goes and its text stays (a pasted span or font), except where the text is itself code or
        // a control: those go whole.
        var sanitizer = new HtmlSanitizer(options) { KeepChildNodes = true };
        sanitizer.RemovingTag += (_, e) =>
        {
            if (DroppedWhole.Contains(e.Tag.LocalName))
            {
                e.Tag.TextContent = string.Empty;
            }
        };

        // A link leaves the page without handing it a reference, and never opens in a frame of ours.
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Dom.IElement { LocalName: "a" } link && link.HasAttribute("href"))
            {
                link.SetAttribute("rel", "noopener noreferrer nofollow");
            }
        };
        return sanitizer;
    }
}

/// <summary>Reads rich text from a JSON string, cleaned; writes it as the cleaned string.</summary>
public sealed class RichTextJsonConverter : JsonConverter<RichText>
{
    public override RichText? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Rich text is a string of HTML.");
        }

        if ((reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length) > RichText.InputMaxLength * 3L)
        {
            throw new JsonException($"Rich text is at most {RichText.InputMaxLength} characters.");
        }

        var html = reader.GetString();
        if (html is { Length: > RichText.InputMaxLength })
        {
            throw new JsonException($"Rich text is at most {RichText.InputMaxLength} characters.");
        }

        return RichText.From(html);
    }

    public override void Write(Utf8JsonWriter writer, RichText value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.Html);
    }
}

/// <summary>Rich text's cleaned HTML is at most so many characters (TF-071 AC1): a length the request limit check reads.</summary>
public sealed class RichTextLengthValidator<T>(int maximum) : PropertyValidator<T, RichText?>, ILengthValidator
{
    public int Min => 0;

    public int Max { get; } = maximum;

    public override string Name => "MaximumLengthValidator";

    public override bool IsValid(ValidationContext<T> context, RichText? value)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (value is null || value.Length <= Max)
        {
            return true;
        }

        context.MessageFormatter.AppendArgument("MaxLength", Max).AppendArgument("TotalLength", value.Length);
        return false;
    }

    protected override string GetDefaultMessageTemplate(string errorCode) =>
        "The length of '{PropertyName}' must be {MaxLength} characters or fewer. You entered {TotalLength} characters.";
}

public static class RichTextRules
{
    /// <summary>The cleaned HTML is at most <paramref name="maximum"/> characters.</summary>
    public static IRuleBuilderOptions<T, RichText?> MaximumLength<T>(this IRuleBuilder<T, RichText?> rule, int maximum)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        return rule.SetValidator(new RichTextLengthValidator<T>(maximum));
    }
}
