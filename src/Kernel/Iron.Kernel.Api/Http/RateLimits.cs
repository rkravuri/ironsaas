using Microsoft.AspNetCore.Builder;

namespace Iron.Kernel.Api.Http;

/// <summary>
/// The rate limits an endpoint is under besides the API's own (TF-072, ADR-0053). Every path under <c>/api</c> and
/// <c>/auth</c> counts against the API limit of its principal, or of its address before sign-in; an endpoint names the
/// others it belongs to, and the Protection module counts and refuses.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Sign-in attempts, per address: 10 a minute by default.</summary>
    public const string Login = "login";

    /// <summary>
    /// Recovery links issued, per account, the person or tenant the route names: 5 an hour by default. Using a link names
    /// no account until its one-time code is checked, so it is under the API's limit alone.
    /// </summary>
    public const string Recovery = "recovery";

    /// <summary>Every call, per principal, or per address before sign-in: 600 a minute by default.</summary>
    public const string Api = "api";

    /// <summary>Making an export, per principal: 20 an hour by default.</summary>
    public const string Exports = "exports";

    /// <summary>Reports of the content security policy, per address: a fixed limit the operator sets, so they cannot flood.</summary>
    public const string CspReports = "csp_reports";

    public static IReadOnlyList<string> All { get; } = [Login, Recovery, Api, Exports, CspReports];
}

/// <summary>
/// An endpoint's membership of a rate limit: the policy, and the route value that names its subject when the limit is per
/// account (the person a recovery link is for), rather than per principal or address.
/// </summary>
public sealed record RateLimitedMetadata(string Policy, string? SubjectRouteValue = null);

public static class RateLimitedExtensions
{
    /// <summary>Puts the endpoint under a rate limit besides the API's own (ADR-0053).</summary>
    public static TBuilder RateLimited<TBuilder>(this TBuilder builder, string policy, string? subjectRouteValue = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!RateLimitPolicies.All.Contains(policy, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{policy}' is not a rate limit. Use one of {string.Join(", ", RateLimitPolicies.All)}.", nameof(policy));
        }

        builder.WithMetadata(new RateLimitedMetadata(policy, subjectRouteValue));
        return builder;
    }
}
