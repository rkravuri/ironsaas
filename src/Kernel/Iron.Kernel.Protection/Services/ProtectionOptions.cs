using System.Diagnostics.Metrics;

namespace Iron.Kernel.Protection.Services;

/// <summary>The operator's side of protection (ADR-0053), from the <c>Protection</c> section: limits' bounds, the challenge, the CSP reports.</summary>
public sealed class ProtectionOptions
{
    public const string SectionName = "Protection";

    /// <summary>Bounds per limit (login, recovery, api, exports) within which a tenant chooses its own.</summary>
    public Dictionary<string, LimitBounds> Limits { get; set; } = new(StringComparer.Ordinal);

    public ChallengeOptions Challenge { get; set; } = new();

    public CspReportOptions CspReports { get; set; } = new();

    public sealed class LimitBounds
    {
        public int Minimum { get; set; }

        public int Maximum { get; set; }
    }

    public sealed class ChallengeOptions
    {
        /// <summary>Failed sign-ins, from one address or for one identifier, after which a sign-in needs a solved challenge.</summary>
        public int AfterFailures { get; set; } = 5;

        public int WindowMinutes { get; set; } = 15;

        /// <summary>The largest number the solver may need: the work is half of it on average (100 000: about a second in a browser).</summary>
        public long MaxNumber { get; set; } = 100_000;

        /// <summary>How long a challenge may be solved; far shorter than the signing key's overlap after a rotation (ADR-0054).</summary>
        public int LifetimeMinutes { get; set; } = 5;
    }

    public sealed class CspReportOptions
    {
        /// <summary>Reports one address may send in a minute; more are refused unread, so the endpoint cannot be flooded.</summary>
        public int PerMinute { get; set; } = 60;

        /// <summary>How many of the latest reports the process keeps for the console.</summary>
        public int Sample { get; set; } = 100;

        /// <summary>Reports written to the log in a minute; the counter counts every one.</summary>
        public int LogPerMinute { get; set; } = 20;

        /// <summary>The largest body the endpoint declares to the server; the operator may set a smaller limit below it.</summary>
        public const int LargestBody = 64 * 1024;

        /// <summary>The largest report read, in bytes.</summary>
        public int MaxBytes { get; set; } = LargestBody;
    }
}

/// <summary>
/// The protection meter (ADR-0053): <c>protection.rate_limited</c> counts refusals by limit, <c>protection.csp_reports</c>
/// counts reports by directive, <c>protection.csp_reports_dropped</c> reports refused by their own limit, and
/// <c>protection.challenges</c> the login challenges issued, solved and refused.
/// </summary>
public sealed class ProtectionMetrics : IDisposable
{
    public const string MeterName = "Iron.Protection";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _rateLimited;
    private readonly Counter<long> _cspReports;
    private readonly Counter<long> _cspDropped;
    private readonly Counter<long> _challenges;

    public ProtectionMetrics()
    {
        _rateLimited = _meter.CreateCounter<long>("protection.rate_limited", description: "Requests refused by a rate limit.");
        _cspReports = _meter.CreateCounter<long>("protection.csp_reports", description: "Reports of the content security policy received.");
        _cspDropped = _meter.CreateCounter<long>("protection.csp_reports_dropped", description: "Reports of the content security policy refused by their own limit.");
        _challenges = _meter.CreateCounter<long>("protection.challenges", description: "Login challenges issued, solved and refused.");
    }

    public void RateLimited(string policy) => _rateLimited.Add(1, new KeyValuePair<string, object?>("limit", policy));

    public void CspReport(string directive) => _cspReports.Add(1, new KeyValuePair<string, object?>("directive", directive));

    public void CspReportDropped() => _cspDropped.Add(1);

    public void Challenge(string outcome) => _challenges.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void Dispose() => _meter.Dispose();
}
