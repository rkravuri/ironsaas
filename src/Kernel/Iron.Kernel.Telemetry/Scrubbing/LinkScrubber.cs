using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Iron.Kernel.Telemetry.Scrubbing;

/// <summary>
/// The second line that keeps invitation and recovery links (ADR-0046) and personal data (TF-069, ADR-0050) out of logs
/// and traces. The first is that no code writes them there: the links travel through the store's handover place and the
/// codes in a URL's fragment, and log lines carry identifiers. This rule catches what slipped past: a link with its code,
/// in a path or a fragment, a code named after "token", and the old path form, each with the code replaced by
/// <c>[redacted]</c>; any text that looks like an email address, replaced by <c>[masked]</c>; and the value of a
/// property named like a name, an email, a phone or an address, masked whatever it holds. Codes are 32 random bytes, so
/// 32 characters or more of base64url is the shape it looks for, which leaves ids and ordinary words alone.
/// </summary>
public static partial class LinkScrubber
{
    public const string Redacted = "[redacted]";

    /// <summary>What stands for personal data in a log line or a span.</summary>
    public const string Masked = "[masked]";

    private static readonly HashSet<string> PersonalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name", "Email", "Mail", "Phone", "Mobile", "Address", "Street", "Postcode", "Birthday",
    };

    /// <summary>The text with every link's or token's code replaced and every email address masked; the text itself when there is none.</summary>
    public static string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var scrubbed = text;
        if (scrubbed.Length >= 32)
        {
            scrubbed = Link().Replace(scrubbed, match => match.Groups["prefix"].Value + Redacted);
            scrubbed = Named().Replace(scrubbed, match => match.Groups["prefix"].Value + Redacted);
        }

        return scrubbed.Contains('@', StringComparison.Ordinal) ? Email().Replace(scrubbed, Masked) : scrubbed;
    }

    /// <summary>Whether the text holds a link's or token's code.</summary>
    public static bool HasCode(string text) => text is { Length: >= 32 } && (Link().IsMatch(text) || Named().IsMatch(text));

    /// <summary>Whether the text holds anything the rule replaces: a code or an email address.</summary>
    public static bool NeedsScrubbing(string text) => HasCode(text) || (text.Contains('@', StringComparison.Ordinal) && Email().IsMatch(text));

    /// <summary>
    /// Whether a property's name says it holds personal data (TF-069 AC3): its last word, in camel case or after a dot or
    /// an underscore, is a name, an email, a phone or an address, as in <c>DisplayName</c>, <c>email</c>, <c>user.phone</c>.
    /// <c>Hostname</c> or <c>ServiceId</c> are not.
    /// </summary>
    public static bool IsPersonal(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        var name = propertyName.Trim('{', '}', '@');
        var cut = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('_'));
        var tail = cut >= 0 ? name[(cut + 1)..] : name;
        var start = 0;
        for (var i = tail.Length - 1; i > 0; i--)
        {
            if (char.IsUpper(tail[i]) && char.IsLower(tail[i - 1]))
            {
                start = i;
                break;
            }
        }

        return PersonalWords.Contains(tail[start..]);
    }

    [GeneratedRegex(@"(?<prefix>/(?:invitations|recovery)(?:/|#|%23))(?<code>[A-Za-z0-9_-]{32,})", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"(?<prefix>(?:invitation|recovery)[ _]?(?:token|code)[""']?\s*[:=]\s*[""']?)(?<code>[A-Za-z0-9_-]{32,})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Named();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
}

/// <summary>
/// Wraps a log provider so every message it writes and every value it keeps passes the scrubbing rule: the console
/// formats with the scrubbed message, and OpenTelemetry exports the scrubbed message and attributes.
/// </summary>
public sealed class ScrubbingLoggerProvider(ILoggerProvider inner, bool ownsInner = true) : ILoggerProvider, ISupportExternalScope
{
    public ILogger CreateLogger(string categoryName) => new ScrubbingLogger(inner.CreateLogger(categoryName));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        if (inner is ISupportExternalScope scoped)
        {
            scoped.SetScopeProvider(scopeProvider);
        }
    }

    public void Dispose()
    {
        if (ownsInner)
        {
            inner.Dispose();
        }
    }

    private sealed class ScrubbingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                // A structured message: its values scrubbed one by one, its message format kept, so the exporters still
                // see the template and the attributes.
                var scrubbed = new ScrubbedState(values, formatter(state, exception));
                inner.Log(logLevel, eventId, scrubbed, exception, static (s, _) => s.Message);
                return;
            }

            inner.Log(logLevel, eventId, state, exception, (s, e) => LinkScrubber.Scrub(formatter(s, e)));
        }
    }

    private sealed class ScrubbedState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly List<KeyValuePair<string, object?>> _values;

        public ScrubbedState(IReadOnlyList<KeyValuePair<string, object?>> values, string message)
        {
            // A personal property's value is masked whatever it holds, and so is its text where the message shows it.
            var personal = values
                .Where(pair => pair.Key != "{OriginalFormat}" && LinkScrubber.IsPersonal(pair.Key) && pair.Value is not null)
                .Select(pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture))
                .Where(text => !string.IsNullOrEmpty(text) && text.Length > 1)
                .Select(text => text!)
                .OrderByDescending(text => text.Length)
                .ToList();
            _values = [.. values.Select(pair => pair.Key == "{OriginalFormat}"
                ? pair
                : LinkScrubber.IsPersonal(pair.Key) && pair.Value is not null
                    ? new KeyValuePair<string, object?>(pair.Key, LinkScrubber.Masked)
                    : pair.Value is string text ? new KeyValuePair<string, object?>(pair.Key, LinkScrubber.Scrub(text)) : pair)];
            Message = LinkScrubber.Scrub(personal.Aggregate(message, (current, text) => current.Replace(text, LinkScrubber.Masked, StringComparison.Ordinal)));
        }

        public string Message { get; }

        public int Count => _values.Count;

        public KeyValuePair<string, object?> this[int index] => _values[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => Message;
    }
}

/// <summary>Passes every string tag of a finished span, and its name, through the scrubbing rule before export; a personal tag is masked.</summary>
public sealed class ScrubbingActivityProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Scrub(data);
    }

    /// <summary>Scrubs the span's name and string tags in place.</summary>
    public static void Scrub(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (LinkScrubber.NeedsScrubbing(activity.DisplayName))
        {
            activity.DisplayName = LinkScrubber.Scrub(activity.DisplayName);
        }

        foreach (var tag in activity.TagObjects.ToList())
        {
            // Tags follow OpenTelemetry's names: only a person's own (enduser.*, user.*) can be personal; server.address is not.
            var ownedByPerson = tag.Key.StartsWith("enduser.", StringComparison.Ordinal) || tag.Key.StartsWith("user.", StringComparison.Ordinal);
            if (ownedByPerson && LinkScrubber.IsPersonal(tag.Key) && tag.Value is not null)
            {
                activity.SetTag(tag.Key, LinkScrubber.Masked);
            }
            else if (tag.Value is string text && LinkScrubber.NeedsScrubbing(text))
            {
                activity.SetTag(tag.Key, LinkScrubber.Scrub(text));
            }
        }
    }
}
