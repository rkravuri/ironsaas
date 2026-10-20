using System.Text.Json;
using System.Text.Json.Serialization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>One difference between a tenant's entitlements and a plan's: what it was (null when added) and what it becomes (null when removed).</summary>
public sealed record EntitlementDifference(EntitlementKind Kind, string Code, EntitlementValue? Before, EntitlementValue? After);

/// <summary>
/// What applying a plan does, or did (TF-042): the plan and grace days before and after, the entitlements added,
/// removed and changed, and the warnings. A dry run answers it without changing anything.
/// </summary>
public sealed record PlanPreview(
    string Tenant,
    string PlanBefore,
    string PlanAfter,
    int GraceDaysBefore,
    int GraceDaysAfter,
    bool Applied,
    int Revision,
    IReadOnlyList<EntitlementDifference> Added,
    IReadOnlyList<EntitlementDifference> Removed,
    IReadOnlyList<EntitlementDifference> Changed,
    IReadOnlyList<string> Warnings)
{
    public bool ChangesAnything => Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0 || PlanBefore != PlanAfter || GraceDaysBefore != GraceDaysAfter;
}

/// <summary>The request to apply a plan: which, from when (default today), until when (default open), and whether only to preview it.</summary>
public sealed record PlanApplication(string Plan, DateOnly? StartsOn, DateOnly? EndsOn, string Reason);

/// <summary>
/// A tenant's entitlements in the control plane (TF-040, TF-042, TF-045): a plan applied in one transaction with its
/// preview, every change in the append-only history with the operator, the values before and after and the revision,
/// the audit event in the same transaction (ADR-0013), and the effective entitlements for the store.
/// </summary>
public sealed class EntitlementService(
    TenancyDbContext context,
    UnitCatalogue catalogue,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal current,
    TimeProvider time,
    FlagPublications flagPublications)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>What applying the plan would change, without changing it.</summary>
    public async Task<Result<PlanPreview>> PreviewAsync(string slug, PlanApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        var plan = await FindPlanAsync(application.Plan, cancellationToken).ConfigureAwait(false);
        if (plan.IsFailure)
        {
            return plan.Error;
        }

        var rows = await RowsAsync(tenant.Id, asTracking: false, cancellationToken).ConfigureAwait(false);
        return Compare(tenant, rows, plan.Value, application, applied: false, tenant.EntitlementsRevision);
    }

    /// <summary>Applies the plan in one transaction (TF-042 AC1); a plan that changes nothing writes nothing.</summary>
    public async Task<Result<PlanPreview>> ApplyAsync(string slug, PlanApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var tenant = await context.Tenants.SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        var applied = await ApplyWithinAsync(tenant, application, cancellationToken).ConfigureAwait(false);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return applied;
    }

    /// <summary>
    /// The apply inside a transaction the caller holds: tenant creation applies the tenant's first plan this way, in the
    /// transaction that creates it.
    /// </summary>
    public async Task<Result<PlanPreview>> ApplyWithinAsync(Tenant tenant, PlanApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(application);
        var plan = await FindPlanAsync(application.Plan, cancellationToken).ConfigureAwait(false);
        if (plan.IsFailure)
        {
            return plan.Error;
        }

        var rows = await RowsAsync(tenant.Id, asTracking: true, cancellationToken).ConfigureAwait(false);
        var preview = Compare(tenant, rows, plan.Value, application, applied: true, tenant.EntitlementsRevision);
        if (preview.IsFailure || !preview.Value.ChangesAnything)
        {
            return preview;
        }

        var difference = preview.Value;
        var revision = tenant.ApplyPlan(plan.Value.Code, plan.Value.GraceDays);
        var byKey = rows.ToDictionary(r => (r.Kind, r.Code));
        foreach (var added in difference.Added)
        {
            context.Entitlements.Add(new TenantEntitlement(tenant.Id, added.Kind, added.Code, added.After!));
        }

        foreach (var changed in difference.Changed)
        {
            byKey[(changed.Kind, changed.Code)].Set(changed.After!);
        }

        foreach (var removed in difference.Removed)
        {
            context.Entitlements.Remove(byKey[(removed.Kind, removed.Code)]);
        }

        var reason = application.Reason;
        if (difference.PlanBefore != difference.PlanAfter)
        {
            context.EntitlementHistory.Add(new EntitlementChange(tenant.Id, tenant.Slug, revision, EntitlementChange.PlanKind, EntitlementChange.PlanKind, Serialize(new { code = difference.PlanBefore }), Serialize(new { code = difference.PlanAfter }), reason));
        }

        if (difference.GraceDaysBefore != difference.GraceDaysAfter)
        {
            context.EntitlementHistory.Add(new EntitlementChange(tenant.Id, tenant.Slug, revision, EntitlementChange.GraceKind, EntitlementChange.GraceKind, Serialize(new { days = difference.GraceDaysBefore }), Serialize(new { days = difference.GraceDaysAfter }), reason));
        }

        foreach (var change in difference.Added.Concat(difference.Changed).Concat(difference.Removed))
        {
            context.EntitlementHistory.Add(new EntitlementChange(tenant.Id, tenant.Slug, revision, KindName(change.Kind), change.Code, Serialize(change.Before), Serialize(change.After), reason));
        }

        // The tenant's flag rules carry its plan for attribute rules (ADR-0035): their store copy is behind now too.
        await flagPublications.TouchAsync(Iron.Kernel.Flags.Contracts.FlagRulesDocument.TenantPath(tenant.Slug), cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan_before"] = difference.PlanBefore,
            ["plan"] = difference.PlanAfter,
            ["revision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["added"] = string.Join(",", difference.Added.Select(d => d.Code)),
            ["removed"] = string.Join(",", difference.Removed.Select(d => d.Code)),
            ["changed"] = string.Join(",", difference.Changed.Select(d => d.Code)),
        };
        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.PlanApplied, AuditOutcome.Success, current.Id, tenant.Slug, details, "tenant", tenant.Id, reason), cancellationToken).ConfigureAwait(false);
        return difference with { Revision = revision };
    }

    /// <summary>The effective entitlements of a tenant as the store keeps them for its unit (ADR-0034).</summary>
    public async Task<EntitlementsDocument?> DocumentAsync(string slug, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return null;
        }

        var rows = await RowsAsync(tenant.Id, asTracking: false, cancellationToken).ConfigureAwait(false);
        var modules = rows.Where(r => r.Kind == EntitlementKind.Module)
            .Select(r => new ModuleEntitlement(r.Code, r.Status, r.SubscriptionType ?? SubscriptionType.Platform, r.UserLimit, r.StartsOn, r.EndsOn))
            .ToList();
        var features = rows.Where(r => r.Kind == EntitlementKind.Feature)
            .Select(r => new FeatureEntitlement(r.Code, r.Status, r.StartsOn, r.EndsOn))
            .ToList();
        return new EntitlementsDocument(EntitlementsDocument.CurrentFormat, tenant.Slug, tenant.EntitlementsRevision, tenant.Plan, tenant.GraceDays, time.GetUtcNow(), modules, features);
    }

    public async Task<IReadOnlyList<TenantEntitlement>> RowsAsync(Guid tenantId, bool asTracking, CancellationToken cancellationToken)
    {
        var query = context.Entitlements.Where(e => e.TenantId == tenantId);
        if (!asTracking)
        {
            query = query.AsNoTracking();
        }

        return await query.OrderBy(e => e.Kind).ThenBy(e => e.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string KindName(EntitlementKind kind) => kind == EntitlementKind.Module ? "module" : "feature";

    public static string? Serialize(object? value) => value is null ? null : JsonSerializer.Serialize(value, Json);

    private async Task<Result<Plan>> FindPlanAsync(string code, CancellationToken cancellationToken)
    {
        var normalised = code.Trim().ToLowerInvariant();
        var plan = await context.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Code == normalised, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return Error.NotFound(TenancyErrors.PlanNotFound, $"There is no plan '{code}'. GET /api/v1/plans lists them.");
        }

        // A plan saved by an older build may name a module or feature this build no longer has (TF-042 AC3).
        var unknown = catalogue.Unknown(plan);
        return unknown.Count > 0
            ? Error.Validation(TenancyErrors.PlanUnknownEntitlements, $"The plan '{plan.Code}' names what this build cannot license: {string.Join(" ", unknown)} Change the plan first.", unknown.Select(problem => new FieldError("plan", "unknown", problem)).ToList())
            : plan;
    }

    private Result<PlanPreview> Compare(Tenant tenant, IReadOnlyList<TenantEntitlement> rows, Plan plan, PlanApplication application, bool applied, int revision)
    {
        if (application.StartsOn is { } starts && application.EndsOn is { } ends && ends < starts)
        {
            return Error.Validation(TenancyErrors.PlanDatesInvalid, $"The subscription would end ({ends:yyyy-MM-dd}) before it starts ({starts:yyyy-MM-dd}).", new FieldError("ends_on", "before_start", "Choose an end date on or after the start date."));
        }

        var existing = rows.ToDictionary(r => (r.Kind, r.Code));
        var target = new Dictionary<(EntitlementKind, string), EntitlementValue>();
        foreach (var module in plan.Modules)
        {
            var declared = catalogue.Catalogue.FindModule(module.Code)!;
            var limit = declared.SubscriptionType == SubscriptionType.PerPerson ? module.UserLimit : null;
            target[(EntitlementKind.Module, module.Code)] = new EntitlementValue(module.Status, declared.SubscriptionType, limit, StartOf(existing, EntitlementKind.Module, module.Code, application), application.EndsOn);
        }

        foreach (var feature in plan.Features)
        {
            target[(EntitlementKind.Feature, feature)] = new EntitlementValue(EntitlementStatus.Enabled, null, null, StartOf(existing, EntitlementKind.Feature, feature, application), application.EndsOn);
        }

        var added = target.Where(t => !existing.ContainsKey(t.Key)).Select(t => new EntitlementDifference(t.Key.Item1, t.Key.Item2, null, t.Value)).ToList();
        var removed = existing.Where(e => !target.ContainsKey(e.Key)).Select(e => new EntitlementDifference(e.Key.Kind, e.Key.Code, e.Value.Value, null)).ToList();
        var changed = target.Where(t => existing.TryGetValue(t.Key, out var row) && row.Value != t.Value).Select(t => new EntitlementDifference(t.Key.Item1, t.Key.Item2, existing[t.Key].Value, t.Value)).ToList();

        var warnings = new List<string>();
        foreach (var gone in removed.Where(r => r.Kind == EntitlementKind.Module))
        {
            warnings.Add($"The module '{gone.Code}' leaves the subscription: its data stays in the tenant's database and is hidden until the module is licensed again.");
        }

        foreach (var off in changed.Where(c => c.Kind == EntitlementKind.Module && c.After!.Status != EntitlementStatus.Enabled && c.Before!.Status == EntitlementStatus.Enabled))
        {
            warnings.Add(off.After!.Status == EntitlementStatus.ReadOnly
                ? $"The module '{off.Code}' becomes read only: its data can be seen and not changed."
                : $"The module '{off.Code}' is switched off: its data stays and is hidden.");
        }

        return new PlanPreview(tenant.Slug, tenant.Plan, plan.Code, tenant.GraceDays, plan.GraceDays, applied, revision, Order(added), Order(removed), Order(changed), warnings);
    }

    /// <summary>A new entitlement starts on the date asked for, or today; one the tenant has keeps its start unless a date is asked for.</summary>
    private DateOnly StartOf(Dictionary<(EntitlementKind, string), TenantEntitlement> existing, EntitlementKind kind, string code, PlanApplication application) =>
        application.StartsOn ?? (existing.TryGetValue((kind, code), out var row) ? row.StartsOn : Today);

    private static List<EntitlementDifference> Order(List<EntitlementDifference> differences) =>
        [.. differences.OrderBy(d => d.Kind).ThenBy(d => d.Code, StringComparer.Ordinal)];
}
