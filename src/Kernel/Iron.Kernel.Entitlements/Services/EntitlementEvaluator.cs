using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Entitlements.Contracts;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Entitlements.Services;

/// <summary>
/// The one effective-entitlement service of the unit (TF-043): <see cref="IEntitlements"/> for product code and the
/// shell's context, and the gate the API's filters ask (ADR-0034). It answers from the cached document and this
/// build's catalogue on the unit's clock; only a per-person module asks the database, for the person's seat, so a seat
/// removed counts on the next request (TF-041 AC2). A refusal is logged, counted and audited.
/// </summary>
public sealed class EntitlementEvaluator(
    EntitlementCache cache,
    EntitlementsOptions options,
    LicensingCatalogue catalogue,
    SeatService seats,
    IAuditWriter audit,
    EntitlementsMetrics metrics,
    TimeProvider time,
    ILogger<EntitlementEvaluator> logger) : IEntitlements, IEntitlementGate
{
    private static readonly Entitlement Unknown = new(EntitlementStatus.Disabled, EntitlementReasons.Unknown);

    /// <summary>The answer in force now, or null when it is missing or older than the limit.</summary>
    public EntitlementSnapshot? Snapshot => cache.Usable(time.GetUtcNow(), options.StaleAfter);

    public DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    public Entitlement Module(string licence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licence);
        return Snapshot is { } snapshot ? EntitlementRules.Module(snapshot.Document, licence, Today) : Unknown;
    }

    public async Task<Entitlement> ModuleForAsync(string licence, Guid principalId, CancellationToken cancellationToken)
    {
        var module = Module(licence);
        if (!module.IsAvailable || catalogue.FindModule(licence)?.SubscriptionType != SubscriptionType.PerPerson)
        {
            return module;
        }

        return await seats.HoldsAsync(licence, principalId, cancellationToken).ConfigureAwait(false)
            ? module
            : new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.NoSeat);
    }

    public Entitlement Feature(string feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        if (Snapshot is not { } snapshot)
        {
            return Unknown;
        }

        // A feature this build does not declare is not licensed, whatever the document says.
        var declared = catalogue.FindFeature(feature);
        if (declared is null)
        {
            return new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.NotLicensed);
        }

        var module = declared.Licence is null ? null : EntitlementRules.Module(snapshot.Document, declared.Licence, Today);
        return EntitlementRules.Feature(snapshot.Document, feature, module, Today);
    }

    public async ValueTask<EntitlementDecision> CheckAsync(EntitlementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Licence is { } licence)
        {
            var module = request.PrincipalId is { } principal && principal != Guid.Empty
                ? await ModuleForAsync(licence, principal, cancellationToken).ConfigureAwait(false)
                : Module(licence);
            var refusal = Refusal(module, request.IsWrite, licence, isFeature: false);
            if (refusal is not null)
            {
                return await RefuseAsync(request, refusal, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var code in request.Features)
        {
            var refusal = Refusal(Feature(code), request.IsWrite, code, isFeature: true);
            if (refusal is not null)
            {
                return await RefuseAsync(request, refusal, cancellationToken).ConfigureAwait(false);
            }
        }

        return EntitlementDecision.Allow;
    }

    private static EntitlementDecision? Refusal(Entitlement entitlement, bool isWrite, string subject, bool isFeature)
    {
        if (!entitlement.IsAvailable)
        {
            return entitlement.Reason switch
            {
                EntitlementReasons.NoSeat => EntitlementDecision.Refuse(EntitlementErrors.SeatRequired, "You have no seat for this module. Ask an administrator to assign you one.", entitlement.Reason, subject),
                EntitlementReasons.Unknown => EntitlementDecision.Refuse(isFeature ? EntitlementErrors.FeatureDisabled : EntitlementErrors.ModuleDisabled, "Your organisation's subscription cannot be confirmed right now, so this is not available. Try again later; if it lasts, tell your administrator.", entitlement.Reason, subject),
                EntitlementReasons.Expired => EntitlementDecision.Refuse(isFeature ? EntitlementErrors.FeatureDisabled : EntitlementErrors.ModuleDisabled, "Your organisation's subscription to this has ended. Ask your administrator to renew it.", entitlement.Reason, subject),
                _ => isFeature
                    ? EntitlementDecision.Refuse(EntitlementErrors.FeatureDisabled, "This feature is not part of your organisation's subscription. Ask your administrator about the plan.", entitlement.Reason, subject)
                    : EntitlementDecision.Refuse(EntitlementErrors.ModuleDisabled, "This module is not part of your organisation's subscription. Ask your administrator about the plan.", entitlement.Reason, subject),
            };
        }

        return isWrite && !entitlement.AllowsWrites
            ? EntitlementDecision.Refuse(EntitlementErrors.ReadOnly, "This is read only for your organisation: you can look, not change. Ask your administrator about the subscription.", entitlement.Reason, subject)
            : null;
    }

    private async Task<EntitlementDecision> RefuseAsync(EntitlementRequest request, EntitlementDecision decision, CancellationToken cancellationToken)
    {
        metrics.Refused(decision.ErrorCode!);
        logger.LogWarning("{Operation} refused with {ErrorCode} for principal {PrincipalId}: {Subject} is {Reason}.", request.Operation, decision.ErrorCode, request.PrincipalId, decision.Subject, decision.Reason);
        try
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["error_code"] = decision.ErrorCode!,
                ["entitlement"] = decision.Subject!,
                ["reason"] = decision.Reason!,
                ["path"] = request.Path,
            };
            await audit.WriteAsync(new AuditEvent(EntitlementsAuditEvents.AccessDenied, AuditOutcome.Denied, request.PrincipalId ?? Guid.Empty, request.Operation, details), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The audit event of an entitlement refusal on {Operation} could not be written.", request.Operation);
        }

        return decision;
    }
}
