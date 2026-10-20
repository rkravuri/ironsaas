using System.Globalization;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api.Flags;

/// <summary>A flag an endpoint needs: on (<c>RequireFlag</c>) or off (<c>RequireFlagOff</c>, a kill switch).</summary>
public sealed record FlagRequirement(string Key, bool MustBeOn);

/// <summary>One flag check of a request: the flags the endpoint names, who asks (nobody on an anonymous endpoint), and the operation for the audit trail.</summary>
public sealed record FlagRequest(IReadOnlyList<FlagRequirement> Flags, Guid? PrincipalId, string Operation, string Path);

/// <summary>The answer of a flag check: allowed, or refused with the error code, a message, the flag and the operator's reason.</summary>
public sealed record FlagDecision(bool Allowed, string? ErrorCode, string? Message, string? Flag, string? Reason)
{
    public static readonly FlagDecision Allow = new(true, null, null, null, null);

    public static FlagDecision Refuse(string errorCode, string message, string flag, string? reason) => new(false, errorCode, message, flag, reason);
}

/// <summary>
/// Decides whether an endpoint's flags allow the request (ADR-0035). Implemented by Iron.Kernel.Flags over OpenFeature,
/// which also writes the audit event of a refusal; the filters here only ask. Registered as a scoped service.
/// </summary>
public interface IFlagGate
{
    /// <summary>The answer the filters act on; a refusal is logged, counted and audited.</summary>
    ValueTask<FlagDecision> CheckAsync(FlagRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The same answer without its side effects, for explaining someone's access (ADR-0039): the flags are evaluated for
    /// the request's principal as <see cref="CheckAsync"/> evaluates them, and nothing is logged, counted or audited.
    /// </summary>
    ValueTask<FlagDecision> DecideAsync(FlagRequest request, CancellationToken cancellationToken) => CheckAsync(request, cancellationToken);
}

/// <summary>Endpoint metadata: the endpoint needs a flag on, or off. <c>RequireFlag</c> and <c>RequireFlagOff</c> put it there.</summary>
public sealed class FlagMetadata(string key, bool mustBeOn)
{
    public string Key { get; } = key;

    public bool MustBeOn { get; } = mustBeOn;
}

/// <summary>The error codes of a refusal. They name the Flags module (ADR-0029), which owns the rule.</summary>
public static class FlagErrors
{
    /// <summary>A kill switch is on: the path is stopped by an operator (503, try again later).</summary>
    public const string Killed = "flags.killed";

    /// <summary>A flag the endpoint needs is off for this caller (404: the operation is not released to them).</summary>
    public const string Disabled = "flags.disabled";
}

public static class FlagEndpointExtensions
{
    /// <summary>
    /// The endpoint runs only while the flag is off: a kill switch (<c>kill.imports</c>) answers 503 <c>flags.killed</c>
    /// with the operator's reason, within a unit's refresh of the rules. Checked after the entitlements and before the
    /// permission (ADR-0035).
    /// </summary>
    public static TBuilder RequireFlagOff<TBuilder>(this TBuilder builder, string key)
        where TBuilder : IEndpointConventionBuilder => Require(builder, key, mustBeOn: false);

    /// <summary>The endpoint runs only while the flag is on for the caller; otherwise 404 <c>flags.disabled</c>.</summary>
    public static TBuilder RequireFlag<TBuilder>(this TBuilder builder, string key)
        where TBuilder : IEndpointConventionBuilder => Require(builder, key, mustBeOn: true);

    private static TBuilder Require<TBuilder>(TBuilder builder, string key, bool mustBeOn)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Flag.IsValidKey(key))
        {
            throw new ArgumentException($"'{key}' is not a flag key.", nameof(key));
        }

        builder.WithMetadata(new FlagMetadata(key, mustBeOn));
        builder.AddEndpointFilter(new FlagFilter());
        return builder;
    }
}

/// <summary>
/// The check for an anonymous endpoint with a flag. A protected endpoint is checked by the permission or sign-in filter
/// instead, after authentication and the entitlements and before the permission (ADR-0035), so this filter lets it pass.
/// </summary>
public sealed class FlagFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var metadata = context.HttpContext.GetEndpoint()?.Metadata;
        if (metadata?.GetMetadata<PermissionMetadata>() is not null || metadata?.GetMetadata<SignedInMetadata>() is not null)
        {
            return await next(context).ConfigureAwait(false);
        }

        return await FlagCheck.RefusalAsync(context.HttpContext, null).ConfigureAwait(false) ?? await next(context).ConfigureAwait(false);
    }
}

/// <summary>The check itself, shared by the filters: an endpoint without flags is no call at all.</summary>
public static class FlagCheck
{
    /// <summary>How long a client is told to wait before it tries a stopped path again: one refresh of the unit's rules.</summary>
    public const int RetryAfterSeconds = 30;

    private const string CheckedKey = "Iron.FlagsChecked";

    /// <summary>
    /// What an endpoint asks of the gate, or null when it names no flag: the request the filter checks and the explainer
    /// decides (ADR-0039), built from the same metadata.
    /// </summary>
    public static FlagRequest? RequestFor(EndpointMetadataCollection metadata, Guid? principalId, string operation, string path)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var requirements = metadata.GetOrderedMetadata<FlagMetadata>().Select(m => new FlagRequirement(m.Key, m.MustBeOn)).Distinct().ToList();
        return requirements.Count == 0 ? null : new FlagRequest(requirements, principalId, operation, path);
    }

    /// <summary>The answer when no gate is registered: a flag nobody can evaluate is off.</summary>
    public static FlagDecision Unconfirmed(FlagRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FlagDecision.Refuse(FlagErrors.Disabled, "This is not available.", request.Flags[0].Key, null);
    }

    /// <summary>The status a refusal answers: 503 for a kill switch, 404 for a flag that is off.</summary>
    public static int StatusOf(FlagDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.ErrorCode == FlagErrors.Killed ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status404NotFound;
    }

    /// <summary>The problem to answer, or null when the endpoint names no flag or every flag allows the request.</summary>
    public static async ValueTask<IResult?> RefusalAsync(HttpContext http, Guid? principalId)
    {
        ArgumentNullException.ThrowIfNull(http);
        var metadata = http.GetEndpoint()?.Metadata;
        var request = metadata is null ? null : RequestFor(metadata, principalId, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/");
        if (request is null || !http.Items.TryAdd(CheckedKey, true))
        {
            return null;
        }

        var gate = http.RequestServices.GetService<IFlagGate>();
        var decision = gate is null ? Unconfirmed(request) : await gate.CheckAsync(request, http.RequestAborted).ConfigureAwait(false);
        if (decision.Allowed)
        {
            return null;
        }

        var killed = decision.ErrorCode == FlagErrors.Killed;
        var error = killed
            ? Error.Unavailable(FlagErrors.Killed, decision.Message ?? "This is stopped by the platform operator for now. Try again later.")
            : Error.NotFound(decision.ErrorCode ?? FlagErrors.Disabled, decision.Message ?? "This is not available.");
        var problem = ProblemFactory.Create(error, http);
        if (decision.Flag is not null)
        {
            problem.Extensions["flag"] = decision.Flag;
        }

        if (decision.Reason is not null)
        {
            problem.Extensions["reason"] = decision.Reason;
        }

        if (killed)
        {
            http.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Problem(problem);
    }
}
