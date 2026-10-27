using System.Net;
using System.Text.Json;
using Iron.Kernel.History;

namespace Iron.Kernel.Protection.Contracts;

/// <summary>The Protection module's permission (ADR-0053): reading the sample of reports of the content security policy.</summary>
public static class ProtectionPermissions
{
    public static readonly Permission CspReportsRead = new("protection.csp_reports.read", "See the latest reports of the content security policy: the page, the rule it broke and what it blocked.", Risk.Medium);

    public static IReadOnlyList<Permission> All => [CspReportsRead];
}

/// <summary>The Protection module's audit events (ADR-0053).</summary>
public static class ProtectionAuditEvents
{
    /// <summary>A request refused by a rate limit: the first refusal of each subject in each window, at low severity.</summary>
    public const string RateLimited = "protection.rate_limited";

    public static IReadOnlyList<AuditEventType> Types { get; } = [new AuditEventType(RateLimited, "Request refused by a rate limit")];
}

public static class ProtectionErrors
{
    public const string RateLimited = "protection.rate_limited";
    public const string CspReportInvalid = "protection.csp_report.invalid";
    public const string CspReportTooLarge = "protection.csp_report.too_large";
}

/// <summary>
/// The bounds the operator sets for the tenant's rate limits (TF-072 AC3, ADR-0053): the module reads them from its
/// configuration at start (<c>Protection:Limits:&lt;limit&gt;:Minimum</c> and <c>Maximum</c>), and a tenant's value outside
/// them is refused when saved and held to them when applied.
/// </summary>
public static class RateLimitBounds
{
    private static readonly Dictionary<string, (int Minimum, int Maximum)> Defaults = new(StringComparer.Ordinal)
    {
        ["login"] = (3, 60),
        ["recovery"] = (1, 20),
        ["api"] = (60, 6000),
        ["exports"] = (1, 200),
    };

    private static volatile Dictionary<string, (int Minimum, int Maximum)> _current = new(Defaults, StringComparer.Ordinal);

    public static (int Minimum, int Maximum) Of(string limit) => _current.TryGetValue(limit, out var bounds) ? bounds : (1, int.MaxValue);

    /// <summary>The module sets the operator's bounds from configuration; a limit it does not name keeps its default.</summary>
    public static void Configure(IReadOnlyDictionary<string, (int Minimum, int Maximum)> bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var next = new Dictionary<string, (int Minimum, int Maximum)>(Defaults, StringComparer.Ordinal);
        foreach (var (limit, value) in bounds)
        {
            if (value.Minimum < 1 || value.Maximum < value.Minimum)
            {
                throw new InvalidOperationException($"Protection:Limits:{limit}: the minimum must be at least 1 and no more than the maximum.");
            }

            next[limit] = value;
        }

        _current = next;
    }

    /// <summary>The value held to the bounds: what applies when the operator narrowed them after the tenant chose.</summary>
    public static int Clamp(string limit, int value)
    {
        var (minimum, maximum) = Of(limit);
        return Math.Clamp(value, minimum, maximum);
    }

    public static bool Allows(string limit, JsonElement value)
    {
        var (minimum, maximum) = Of(limit);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= minimum && number <= maximum;
    }
}

/// <summary>
/// The tenant's protection settings (TF-070 AC2, TF-072 AC3, ADR-0053): the origins that may call its API from a browser,
/// and its rate limits, within the operator's bounds. Each needs a recent sign-in to change, and applies within a minute.
/// </summary>
public static class ProtectionSettings
{
    public const int MaxOrigins = 20;
    public const int MaxOriginLength = 200;

    public static readonly Setting<JsonElement> AllowedOrigins = Setting.Json(
        "protection.cors.allowed_origins",
        "The web origins, such as https://portal.example.com, whose pages may call this organisation's API from a browser. Empty: only the organisation's own pages.",
        "[]",
        SettingScopes.Tenant,
        SettingRule.Must(IsOriginList, $"A list of at most {MaxOrigins} origins, each a scheme and a host with an optional port, like https://portal.example.com, without a path."))
        .RequiringRecentAuthentication();

    public static readonly Setting<int> LoginPerMinute = Limit("login", "protection.limits.login_per_minute", "How many sign-in attempts one address may make in a minute.", 10, "a minute");

    public static readonly Setting<int> RecoveryPerHour = Limit("recovery", "protection.limits.recovery_per_hour", "How many recovery links may be issued for one account, or used from one address, in an hour.", 5, "an hour");

    public static readonly Setting<int> ApiPerMinute = Limit("api", "protection.limits.api_per_minute", "How many calls one person or client, or one address before sign-in, may make to the API in a minute.", 600, "a minute");

    public static readonly Setting<int> ExportsPerHour = Limit("exports", "protection.limits.exports_per_hour", "How many exports one person or client may make in an hour.", 20, "an hour");

    public static IReadOnlyList<SettingDefinition> All => [AllowedOrigins, LoginPerMinute, RecoveryPerHour, ApiPerMinute, ExportsPerHour];

    /// <summary>The setting of a rate limit, by the limit's name.</summary>
    public static Setting<int>? OfLimit(string limit) => limit switch
    {
        "login" => LoginPerMinute,
        "recovery" => RecoveryPerHour,
        "api" => ApiPerMinute,
        "exports" => ExportsPerHour,
        _ => null,
    };

    /// <summary>The origins of a value, each as the browser sends it (no trailing slash, lower case).</summary>
    public static IReadOnlyList<string> OriginsOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.String).Select(o => o.GetString()!.TrimEnd('/').ToLowerInvariant()).Distinct(StringComparer.Ordinal)]
            : [];

    public static bool IsOriginList(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxOrigins)
        {
            return false;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= MaxOriginLength } text || !IsOrigin(text))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An origin: http or https, a host, an optional port, and nothing else; http only for a host on this machine.</summary>
    public static bool IsOrigin(string text)
    {
        if (!Uri.TryCreate(text.TrimEnd('/'), UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "localhost"));
    }

    private static Setting<int> Limit(string limit, string key, string description, int defaultValue, string per)
    {
        var (minimum, maximum) = RateLimitBounds.Of(limit);
        return Setting.WholeNumber(
            key,
            description,
            defaultValue,
            SettingRule.Must(value => RateLimitBounds.Allows(limit, value), $"A whole number within the bounds your platform operator set, by default from {minimum} to {maximum} {per}."),
            SettingScopes.Tenant)
            .RequiringRecentAuthentication();
    }
}

/// <summary>A challenge of the login's proof of work, as the client solves it (ALTCHA's form, ADR-0053).</summary>
public sealed record LoginChallenge(string Algorithm, string Challenge, long MaxNumber, string Salt, string Signature);

/// <summary>
/// The login's challenge (TF-072, ADR-0053): after repeated failures from an address or for one identifier, a sign-in
/// must carry a solved proof of work, which the server issued and checks once. Counted in this process for now; S6, with
/// several replicas per unit, moves the counts and the used challenges to a shared store.
/// </summary>
public interface ILoginChallenges
{
    /// <summary>Whether a sign-in from this address for this identifier must carry a solved challenge now.</summary>
    bool IsRequired(IPAddress? address, string loginId);

    /// <summary>
    /// A new challenge, valid for a few minutes. Its work doubles with each failure from the address past the threshold,
    /// up to sixteen times: the progressive delay of TF-072.
    /// </summary>
    LoginChallenge Issue(IPAddress? address);

    /// <summary>True for a solution of a challenge this server issued, still valid and not used before; it is used up.</summary>
    bool Verify(string? solution);

    /// <summary>Counts a failed sign-in for the address and the identifier.</summary>
    void Failed(IPAddress? address, string loginId);

    /// <summary>A sign-in that succeeded: the identifier's count starts again; the address's stays.</summary>
    void Succeeded(string loginId);
}
