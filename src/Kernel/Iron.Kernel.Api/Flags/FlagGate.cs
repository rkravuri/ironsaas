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
    ValueTask<FlagDecision> CheckAsync(FlagRequest request, CancellationToken cancellationToken);
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

    /// <summary>The problem to answer, or null when the endpoint names no flag or every flag allows the request.</summary>
    public static async ValueTask<IResult?> RefusalAsync(HttpContext http, Guid? principalId)
    {
        ArgumentNullException.ThrowIfNull(http);
        var requirements = http.GetEndpoint()?.Metadata.GetOrderedMetadata<FlagMetadata>()
            .Select(m => new FlagRequirement(m.Key, m.MustBeOn))
            .Distinct()
            .ToList();
        if (requirements is null || requirements.Count == 0 || !http.Items.TryAdd(CheckedKey, true))
        {
            return null;
        }

        var gate = http.RequestServices.GetService<IFlagGate>();
        var request = new FlagRequest(requirements, principalId, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/");
        var decision = gate is null
            ? FlagDecision.Refuse(FlagErrors.Disabled, "This is not available.", requirements[0].Key, null)
            : await gate.CheckAsync(request, http.RequestAborted).ConfigureAwait(false);
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
