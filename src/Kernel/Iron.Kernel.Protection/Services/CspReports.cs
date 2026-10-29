using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Protection.Services;

/// <summary>One violation of the content security policy, as a browser reported it: where, which rule, what, and a sample.</summary>
public sealed record CspViolation(DateTimeOffset ReceivedAt, string Page, string Directive, string Blocked, string? Sample, string? Disposition);

/// <summary>
/// The reports of the content security policy (TF-070 AC3, ADR-0053). Browsers send them to the report endpoint in either
/// form: <c>application/csp-report</c> (<c>report-uri</c>) or <c>application/reports+json</c> (<c>report-to</c>). Each is
/// counted by its directive; the latest are kept for the console, and a few a minute go to the log. The page is kept
/// without its query, so a link's code never lands here.
/// </summary>
public sealed class CspReports(ProtectionOptions options, ProtectionMetrics metrics, TimeProvider time, ILogger<CspReports> logger)
{
    private const int MaxText = 300;

    private readonly ConcurrentQueue<CspViolation> _sample = new();
    private ConcurrentDictionary<string, int> _pending = new(StringComparer.Ordinal);
    private readonly Lock _logLock = new();
    private DateTimeOffset _logMinute = DateTimeOffset.MinValue;
    private int _loggedThisMinute;

    public IReadOnlyList<CspViolation> Latest => [.. _sample.Reverse()];

    /// <summary>The reports since the last call, by directive, for the minute's summary event (ADR-0056).</summary>
    public IReadOnlyDictionary<string, int> TakePending() => Interlocked.Exchange(ref _pending, new ConcurrentDictionary<string, int>(StringComparer.Ordinal));

    /// <summary>Reads a report's body; false when it is no report of either form.</summary>
    public bool Accept(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var violations = Parse(document.RootElement).ToList();
        if (violations.Count == 0)
        {
            return false;
        }

        foreach (var violation in violations)
        {
            metrics.CspReport(violation.Directive);
            _pending.AddOrUpdate(violation.Directive, 1, (_, count) => count + 1);
            _sample.Enqueue(violation);
            while (_sample.Count > Math.Max(1, options.CspReports.Sample) && _sample.TryDequeue(out _))
            {
            }

            if (MayLog())
            {
                logger.LogWarning("CSP violation: {Directive} blocked {Blocked} on {Page}; sample {Sample}.", violation.Directive, violation.Blocked, violation.Page, violation.Sample ?? "none");
            }
        }

        return true;
    }

    private bool MayLog()
    {
        var now = time.GetUtcNow();
        lock (_logLock)
        {
            if (now - _logMinute >= TimeSpan.FromMinutes(1))
            {
                _logMinute = now;
                _loggedThisMinute = 0;
            }

            return ++_loggedThisMinute <= options.CspReports.LogPerMinute;
        }
    }

    private IEnumerable<CspViolation> Parse(JsonElement root)
    {
        // report-uri: { "csp-report": { "document-uri": ..., "effective-directive": ..., "blocked-uri": ..., "script-sample": ... } }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("csp-report", out var report) && report.ValueKind == JsonValueKind.Object)
        {
            yield return Violation(
                Text(report, "document-uri"),
                Text(report, "effective-directive") ?? Text(report, "violated-directive"),
                Text(report, "blocked-uri"),
                Text(report, "script-sample"),
                Text(report, "disposition"));
            yield break;
        }

        // report-to: [ { "type": "csp-violation", "url": ..., "body": { "documentURL": ..., "effectiveDirective": ..., "blockedURL": ..., "sample": ... } } ]
        if (root.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in root.EnumerateArray().Take(50))
        {
            if (item.ValueKind == JsonValueKind.Object && Text(item, "type") == "csp-violation" && item.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
            {
                yield return Violation(
                    Text(body, "documentURL") ?? Text(item, "url"),
                    Text(body, "effectiveDirective"),
                    Text(body, "blockedURL"),
                    Text(body, "sample"),
                    Text(body, "disposition"));
            }
        }
    }

    private CspViolation Violation(string? page, string? directive, string? blocked, string? sample, string? disposition) =>
        new(time.GetUtcNow(), PathOf(page), Clip(directive) ?? "unknown", Clip(PathOf(blocked)) ?? "unknown", Clip(sample), Clip(disposition));

    /// <summary>A page or a blocked address without its query and fragment; a word such as <c>inline</c> as it is.</summary>
    private static string PathOf(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return "unknown";
        }

        var end = address.IndexOfAny(['?', '#']);
        return Clip(end < 0 ? address : address[..end])!;
    }

    private static string? Clip(string? text) => text is null ? null : text.Length <= MaxText ? text : text[..MaxText];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
