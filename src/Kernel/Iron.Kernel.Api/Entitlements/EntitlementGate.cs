using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api.Entitlements;

/// <summary>
/// One entitlement check (TF-040, TF-043, TF-046): the licensed module the endpoint belongs to, if any, the features it
/// names, whether it writes, and who asks (nobody on an anonymous endpoint), with the operation for the audit trail.
/// </summary>
public sealed record EntitlementRequest(string? Licence, IReadOnlyList<string> Features, bool IsWrite, Guid? PrincipalId, string Operation, string Path);

/// <summary>The answer of a check: allowed, or refused with the error code and a message for the person.</summary>
public sealed record EntitlementDecision(bool Allowed, string? ErrorCode, string? Message, string? Reason, string? Subject)
{
    public static readonly EntitlementDecision Allow = new(true, null, null, null, null);

    public static EntitlementDecision Refuse(string errorCode, string message, string reason, string subject) => new(false, errorCode, message, reason, subject);
}

/// <summary>
/// Decides whether an endpoint's module and features are entitled for this tenant (and, for a per-person module, this
/// person). Implemented by Iron.Kernel.Entitlements, which also writes the audit event of a refusal; the filters here
/// only ask, as they ask <see cref="IPermissionEvaluator"/>. Registered as a scoped service.
/// </summary>
public interface IEntitlementGate
{
    /// <summary>The answer the filters act on; a refusal is logged, counted and audited.</summary>
    ValueTask<EntitlementDecision> CheckAsync(EntitlementRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The same answer without its side effects, for explaining someone's access (ADR-0039): the gate's check runs as
    /// <see cref="CheckAsync"/> runs it, and nothing is logged, counted or audited. A gate with no side effects needs
    /// nothing more.
    /// </summary>
    ValueTask<EntitlementDecision> DecideAsync(EntitlementRequest request, CancellationToken cancellationToken) => CheckAsync(request, cancellationToken);
}

/// <summary>Endpoint metadata: the endpoint belongs to a licensed module. MapModules puts it on every endpoint of such a module.</summary>
public sealed class LicensedModuleMetadata(string licence)
{
    public string Licence { get; } = licence;
}

/// <summary>Endpoint metadata: the endpoint needs a feature (TF-046). <c>RequireFeature</c> puts it there.</summary>
public sealed class FeatureMetadata(string feature)
{
    public string Feature { get; } = feature;
}

/// <summary>The error codes of a refusal. They name the Entitlements module (ADR-0029), which owns the rule.</summary>
public static class EntitlementErrors
{
    public const string ModuleDisabled = "entitlements.module_disabled";
    public const string ReadOnly = "entitlements.read_only";
    public const string FeatureDisabled = "entitlements.feature_disabled";
    public const string SeatRequired = "entitlements.seat_required";
}

public static class EntitlementEndpointExtensions
{
    /// <summary>
    /// The endpoint needs a feature (TF-046): refused with 403 <c>entitlements.feature_disabled</c> when the feature, or the
    /// module it belongs to, is not available. Checked after authentication and before the permission (ADR-0034).
    /// </summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string feature)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Feature.IsValidCode(feature))
        {
            throw new ArgumentException($"'{feature}' is not a feature code.", nameof(feature));
        }

        builder.WithMetadata(new FeatureMetadata(feature));
        builder.AddEndpointFilter(new EntitlementFilter());
        return builder;
    }
}

/// <summary>
/// The check for an anonymous endpoint of a licensed module or with a feature. A protected endpoint is checked by the
/// permission or sign-in filter instead, after authentication and before the permission (ADR-0034), so this filter
/// lets it pass.
/// </summary>
public sealed class EntitlementFilter : IEndpointFilter
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

        return await EntitlementCheck.RefusalAsync(context.HttpContext, null).ConfigureAwait(false) ?? await next(context).ConfigureAwait(false);
    }
}

/// <summary>The check itself, shared by the filters: nothing to check is no call at all.</summary>
public static class EntitlementCheck
{
    private const string CheckedKey = "Iron.EntitlementsChecked";

    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options];

    /// <summary>A write is any method but GET, HEAD and OPTIONS: a read-only module refuses it (TF-040 AC2).</summary>
    public static bool IsWrite(string method) => !ReadMethods.Contains(method, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What an endpoint asks of the gate, or null when it names nothing licensed: the request the filter checks and the
    /// explainer decides (ADR-0039), built from the same metadata.
    /// </summary>
    public static EntitlementRequest? RequestFor(EndpointMetadataCollection metadata, string method, Guid? principalId, string operation, string path)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var licence = metadata.GetMetadata<LicensedModuleMetadata>()?.Licence;
        var features = metadata.GetOrderedMetadata<FeatureMetadata>().Select(f => f.Feature).Distinct(StringComparer.Ordinal).ToList();
        return licence is null && features.Count == 0 ? null : new EntitlementRequest(licence, features, IsWrite(method), principalId, operation, path);
    }

    /// <summary>The answer when no gate is registered: something licensed that cannot be confirmed is refused.</summary>
    public static EntitlementDecision Unconfirmed(EntitlementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EntitlementDecision.Refuse(request.Licence is null ? EntitlementErrors.FeatureDisabled : EntitlementErrors.ModuleDisabled, "This is not part of your organisation's subscription.", EntitlementReasons.Unknown, request.Licence ?? request.Features[0]);
    }

    /// <summary>The problem to answer, or null when the endpoint names nothing licensed or everything it names is entitled.</summary>
    public static async ValueTask<IResult?> RefusalAsync(HttpContext http, Guid? principalId)
    {
        ArgumentNullException.ThrowIfNull(http);
        var metadata = http.GetEndpoint()?.Metadata;
        if (metadata is null)
        {
            return null;
        }

        var request = RequestFor(metadata, http.Request.Method, principalId, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/");
        if (request is null || !http.Items.TryAdd(CheckedKey, true))
        {
            // Nothing licensed, or a filter of an outer group checked this request already.
            return null;
        }

        var gate = http.RequestServices.GetService<IEntitlementGate>();
        var decision = gate is null ? Unconfirmed(request) : await gate.CheckAsync(request, http.RequestAborted).ConfigureAwait(false);
        if (decision.Allowed)
        {
            return null;
        }

        var problem = ProblemFactory.Create(Error.Forbidden(decision.ErrorCode ?? EntitlementErrors.ModuleDisabled, decision.Message ?? "This is not part of your organisation's subscription."), http);
        if (decision.Subject is not null)
        {
            problem.Extensions["entitlement"] = decision.Subject;
        }

        if (decision.Reason is not null)
        {
            problem.Extensions["reason"] = decision.Reason;
        }

        return TypedResults.Problem(problem);
    }
}
