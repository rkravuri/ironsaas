using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Iron.Kernel.Telemetry.Scrubbing;

/// <summary>
/// The second line that keeps invitation and recovery links out of logs and traces (ADR-0046). The first is that no code
/// writes them there: the links travel through the store's handover place and the codes in a URL's fragment. This rule
/// catches what slipped past: a link with its code, in a path or a fragment, a code named after "token", and the old
/// path form, each with the code replaced by <c>[redacted]</c>. Codes are 32 random bytes, so 32 characters or more of
/// base64url is the shape it looks for, which leaves ids and ordinary words alone.
/// </summary>
public static partial class LinkScrubber
{
    public const string Redacted = "[redacted]";

    /// <summary>The text with every link's or token's code replaced; the text itself when there is none.</summary>
    public static string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length < 32)
        {
            return text;
        }

        var scrubbed = Link().Replace(text, match => match.Groups["prefix"].Value + Redacted);
        return Named().Replace(scrubbed, match => match.Groups["prefix"].Value + Redacted);
    }

    /// <summary>Whether the text holds a link's or token's code.</summary>
    public static bool HasCode(string text) => text is { Length: >= 32 } && (Link().IsMatch(text) || Named().IsMatch(text));

    [GeneratedRegex(@"(?<prefix>/(?:invitations|recovery)(?:/|#|%23))(?<code>[A-Za-z0-9_-]{32,})", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"(?<prefix>(?:invitation|recovery)[ _]?(?:token|code)[""']?\s*[:=]\s*[""']?)(?<code>[A-Za-z0-9_-]{32,})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Named();
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
            _values = [.. values.Select(pair => pair.Value is string text && pair.Key != "{OriginalFormat}" ? new KeyValuePair<string, object?>(pair.Key, LinkScrubber.Scrub(text)) : pair)];
            Message = LinkScrubber.Scrub(message);
        }

        public string Message { get; }

        public int Count => _values.Count;

        public KeyValuePair<string, object?> this[int index] => _values[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => Message;
    }
}

/// <summary>Passes every string tag of a finished span, and its name, through the scrubbing rule before export.</summary>
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
        if (LinkScrubber.HasCode(activity.DisplayName))
        {
            activity.DisplayName = LinkScrubber.Scrub(activity.DisplayName);
        }

        foreach (var tag in activity.TagObjects.ToList())
        {
            if (tag.Value is string text && LinkScrubber.HasCode(text))
            {
                activity.SetTag(tag.Key, LinkScrubber.Scrub(text));
            }
        }
    }
}
