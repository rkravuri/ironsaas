using Iron.Kernel.Api.Flags;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Flags.Contracts;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Flags.Services;

/// <summary>
/// The gate the API's filters ask (ADR-0035): each flag of the endpoint through OpenFeature, with the request's
/// context. A kill switch that is on refuses with <c>flags.killed</c> and the operator's reason; a flag the endpoint needs
/// on refuses with <c>flags.disabled</c>. A refusal is logged, counted and audited.
/// </summary>
public sealed class FlagGate(IFlagClients clients, IAuditWriter audit, FlagsMetrics metrics, ILogger<FlagGate> logger) : IFlagGate
{
    public async ValueTask<FlagDecision> CheckAsync(FlagRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = await clients.ForAsync(request.PrincipalId ?? Guid.Empty, cancellationToken).ConfigureAwait(false);
        foreach (var requirement in request.Flags)
        {
            // Off is the default the caller gives: a kill switch nobody knows about lets the request through, a release flag does not.
            var details = await client.GetBooleanDetailsAsync(requirement.Key, false, null, null, cancellationToken).ConfigureAwait(false);
            if (details.Value == requirement.MustBeOn)
            {
                continue;
            }

            var reason = details.FlagMetadata?.GetString(IronFlagProvider.RuleReasonKey);
            var decision = requirement.Key.StartsWith(Flag.KillSwitchPrefix, StringComparison.Ordinal)
                ? FlagDecision.Refuse(FlagErrors.Killed, Killed(requirement.Key, reason), requirement.Key, reason)
                : FlagDecision.Refuse(FlagErrors.Disabled, "This is not available to you yet.", requirement.Key, reason);
            await RefuseAsync(request, decision, cancellationToken).ConfigureAwait(false);
            return decision;
        }

        return FlagDecision.Allow;
    }

    /// <summary>The words of a stopped path: what, why when the operator said, and what to do.</summary>
    public static string Killed(string key, string? reason)
    {
        var path = key[Flag.KillSwitchPrefix.Length..];
        var what = char.ToUpperInvariant(path[0]) + path[1..];
        return reason is null
            ? $"{what} are stopped by the platform operator for now. Try again later."
            : $"{what} are stopped by the platform operator for now: {reason} Try again later.";
    }

    private async Task RefuseAsync(FlagRequest request, FlagDecision decision, CancellationToken cancellationToken)
    {
        metrics.Refused(decision.ErrorCode!);
        logger.LogWarning("{Operation} refused with {ErrorCode} for principal {PrincipalId}: flag {Flag}.", request.Operation, decision.ErrorCode, request.PrincipalId, decision.Flag);
        try
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["error_code"] = decision.ErrorCode!,
                ["flag"] = decision.Flag!,
                ["path"] = request.Path,
            };
            if (decision.Reason is not null)
            {
                details["reason"] = decision.Reason;
            }

            await audit.WriteAsync(new AuditEvent(FlagsAuditEvents.AccessDenied, AuditOutcome.Denied, request.PrincipalId ?? Guid.Empty, request.Operation, details), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The audit event of a flag refusal on {Operation} could not be written.", request.Operation);
        }
    }
}
