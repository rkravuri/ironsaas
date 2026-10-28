using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Iron.Kernel.Telemetry.Scrubbing;

/// <summary>
/// The second line that keeps invitation and recovery links (ADR-0046), personal data (TF-069, ADR-0050) and secrets
/// (TF-073 AC3, ADR-0054) out of logs and traces. The first is that no code writes them there: the links travel through
/// the store's handover place and the codes in a URL's fragment, secrets come from the store and stay in memory, and log
/// lines carry identifiers. This rule catches what slipped past: a link with its code, in a path or a fragment, a code
/// named after "token", and the old path form, each with the code replaced by <c>[redacted]</c>; a connection string's
/// password or account key, a signature or token in a query string, a bearer token, a JSON Web Token, a private key in
/// PEM, a store token, and the value of anything named a password, a secret, a key or a token, each replaced by
/// <c>[redacted]</c>; any text that looks like an email address, replaced by <c>[masked]</c>; and the value of a property
/// named like a name, an email, a phone or an address, masked whatever it holds. Codes are 32 random bytes, so 32
/// characters or more of base64url is the shape it looks for, which leaves ids and ordinary words alone.
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

    /// <summary>The text with every link's code and every secret replaced and every email address masked; the text itself when there is none.</summary>
    public static string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var scrubbed = text;
        if (scrubbed.Length >= 32)
        {
            scrubbed = Link().Replace(scrubbed, match => match.Groups["prefix"].Value + Redacted);
            scrubbed = Named().Replace(scrubbed, match => match.Groups["prefix"].Value + Redacted);
        }

        scrubbed = ScrubSecrets(scrubbed);
        return scrubbed.Contains('@', StringComparison.Ordinal) ? Email().Replace(scrubbed, Masked) : scrubbed;
    }

    /// <summary>Whether the text holds a link's or token's code.</summary>
    public static bool HasCode(string text) => text is { Length: >= 32 } && (Link().IsMatch(text) || Named().IsMatch(text));

    /// <summary>Whether the text holds a secret the rule replaces (TF-073 AC3).</summary>
    public static bool HasSecret(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length >= 8 && !ReferenceEquals(ScrubSecrets(text), text);
    }

    /// <summary>Whether the text holds anything the rule replaces: a code, a secret or an email address.</summary>
    public static bool NeedsScrubbing(string text) => HasCode(text) || HasSecret(text) || (text.Contains('@', StringComparison.Ordinal) && Email().IsMatch(text));

    /// <summary>The text with every secret the rule knows replaced; the same instance when there is none, which <see cref="HasSecret"/> relies on.</summary>
    private static string ScrubSecrets(string text)
    {
        if (text.Length < 8)
        {
            return text;
        }

        var scrubbed = text;
        if (scrubbed.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            scrubbed = Swap(scrubbed, PrivateKey().Replace(scrubbed, Redacted));
        }

        if (scrubbed.Contains("eyJ", StringComparison.Ordinal))
        {
            scrubbed = Swap(scrubbed, JsonWebToken().Replace(scrubbed, Redacted));
        }

        if (scrubbed.Contains('=', StringComparison.Ordinal) || scrubbed.Contains(':', StringComparison.Ordinal))
        {
            scrubbed = Swap(scrubbed, ConnectionSecret().Replace(scrubbed, match => match.Groups["prefix"].Value + Redacted));
            scrubbed = Swap(scrubbed, QuerySecret().Replace(scrubbed, match => match.Groups["prefix"].Value + Redacted));
            scrubbed = Swap(scrubbed, NamedSecret().Replace(scrubbed, match => match.Groups["prefix"].Value + Redacted));
        }

        if (scrubbed.Contains("earer ", StringComparison.OrdinalIgnoreCase) || scrubbed.Contains("asic ", StringComparison.OrdinalIgnoreCase))
        {
            scrubbed = Swap(scrubbed, Authorization().Replace(scrubbed, match => match.Groups["prefix"].Value + Redacted));
        }

        if (scrubbed.Contains('.', StringComparison.Ordinal))
        {
            scrubbed = Swap(scrubbed, StoreToken().Replace(scrubbed, Redacted));
        }

        return scrubbed;
    }

    // Regex.Replace answers the same instance when nothing matched; equal text means no change, whatever instance came back.
    private static string Swap(string before, string after) => string.Equals(before, after, StringComparison.Ordinal) ? before : after;

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

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|$)", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex JsonWebToken();

    // A connection string's secret parts: PostgreSQL's password, a storage account's key, a shared access signature.
    [GeneratedRegex(@"(?<prefix>\b(?:password|pwd|accountkey|sharedaccesskey|sharedaccesssignature)\s*=\s*)(?:""[^""]*""|'[^']*'|[^;\s""']+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionSecret();

    // A query string's secret values: a SAS signature, an access token, a client secret, an authorisation code.
    [GeneratedRegex(@"(?<prefix>[?&](?:sig|signature|access_token|refresh_token|id_token|client_secret|code_verifier|code|token)=)[^&\s""'#]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QuerySecret();

    // Anything named a password, a secret, a key or a token, as configuration, JSON or a header writes it.
    [GeneratedRegex(@"(?<prefix>[""']?\b(?:password|passwd|secret|client[_-]?secret|clientsecret|api[_-]?key|access[_-]?key|private[_-]?key|account[_-]?key|factor[_-]?key|signing[_-]?key[_-]?pem|unit[_-]?token|vault[_-]?token|x-vault-token|access[_-]?token|refresh[_-]?token|id[_-]?token|token)[""']?\s*[:=]\s*[""']?)(?<secret>[^""'\s,;&}]{4,})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NamedSecret();

    [GeneratedRegex(@"(?<prefix>\b(?:bearer|basic)\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Authorization();

    // A store token: OpenBao's and Vault's service, batch and recovery tokens.
    [GeneratedRegex(@"\b(?:hvs|hvb|hvr|s|b|r)\.[A-Za-z0-9]{24,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex StoreToken();
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

            // The exception's text goes out too, the console's whole and OpenTelemetry's message and stack trace: one
            // whose text holds a secret goes out as its scrubbed stand-in (TF-073 AC3).
            var safe = exception is null ? null : ScrubbedException.From(exception);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                // A structured message: its values scrubbed one by one, its message format kept, so the exporters still
                // see the template and the attributes.
                var scrubbed = new ScrubbedState(values, formatter(state, exception));
                inner.Log(logLevel, eventId, scrubbed, safe, static (s, _) => s.Message);
                return;
            }

            inner.Log(logLevel, eventId, state, safe, (s, e) => LinkScrubber.Scrub(formatter(s, e)));
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

/// <summary>
/// An exception as logs and traces carry it (TF-073 AC3, ADR-0054): its message, its stack trace and its whole text with
/// every secret and code scrubbed, and the original's type named. Made only for an exception whose text needs it.
/// </summary>
public sealed class ScrubbedException : Exception
{
    private readonly string? _text;
    private readonly string? _stackTrace;

    public ScrubbedException()
    {
    }

    public ScrubbedException(string message)
        : base(message)
    {
    }

    public ScrubbedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private ScrubbedException(Exception original)
        : base(LinkScrubber.Scrub(original.Message))
    {
        OriginalType = original.GetType().FullName ?? original.GetType().Name;
        _text = LinkScrubber.Scrub(original.ToString());
        _stackTrace = original.StackTrace is { } trace ? LinkScrubber.Scrub(trace) : null;
    }

    /// <summary>The type of the exception this one stands in for.</summary>
    public string OriginalType { get; } = string.Empty;

    public override string? StackTrace => _stackTrace ?? base.StackTrace;

    /// <summary>The exception that needs no scrubbing, or its scrubbed stand-in.</summary>
    public static Exception From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is ScrubbedException || !LinkScrubber.NeedsScrubbing(exception.ToString()) ? exception : new ScrubbedException(exception);
    }

    public override string ToString() => _text ?? base.ToString();
}

/// <summary>Passes every string tag of a finished span, and its name, through the scrubbing rule before export; a personal tag is masked.</summary>
public sealed class ScrubbingActivityProcessor : BaseProcessor<Activity>
{
    /// <summary>
    /// Records an exception on a span as its scrubbed stand-in (TF-073 AC3): a span's events cannot be changed once added,
    /// so the request's exception is recorded through here, never as it is.
    /// </summary>
    public static void RecordException(Activity activity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(exception);
        activity.AddException(ScrubbedException.From(exception));
    }

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
