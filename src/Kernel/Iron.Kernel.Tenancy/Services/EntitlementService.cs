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
/// removed and changed, and the warnings. A dry run answers it without changing anything. A plan changes only the
/// entitlements of its own group, the Foundation's or one suite's (TF-244), which <see cref="Suite"/> names.
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
    IReadOnlyList<string> Warnings,
    string Suite = SuiteName.Foundation)
{
    public bool ChangesAnything => Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0 || PlanBefore != PlanAfter || GraceDaysBefore != GraceDaysAfter;
}

/// <summary>The request to apply a plan: which, from when (default today), until when (default open), and whether only to preview it.</summary>
public sealed record PlanApplication(string Plan, DateOnly? StartsOn, DateOnly? EndsOn, string Reason);

/// <summary>
/// A tenant's entitlements in the control plane (TF-040, TF-042, TF-045): a plan applied in one transaction with its
/// preview, every change in the append-only history with the operator, the values before and after and the revision,
/// the audit event in the same transaction (ADR-0013), and the effective entitlements for the store. Since TF-244 a
/// tenant holds one Foundation plan and one plan per suite it has; each plan changes only its own group's entitlements,
/// and the store's document is the union of them all, each entry with its own plan's grace days (ADR-0059).
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

        var group = await GroupAsync(tenant, plan.Value.Suite, tracking: false, cancellationToken).ConfigureAwait(false);
        if (group.IsFailure)
        {
            return group.Error;
        }

        var rows = await RowsAsync(tenant.Id, asTracking: false, cancellationToken, plan.Value.Suite).ConfigureAwait(false);
        return Compare(tenant, group.Value, rows, plan.Value, application, applied: false, tenant.EntitlementsRevision);
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
    /// The apply inside a transaction the caller holds: tenant creation applies the tenant's first plans this way, in the
    /// transaction that creates it. A suite's plan applies only to a tenant that has the suite (TF-244).
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

        var group = await GroupAsync(tenant, plan.Value.Suite, tracking: true, cancellationToken).ConfigureAwait(false);
        if (group.IsFailure)
        {
            return group.Error;
        }

        var rows = await RowsAsync(tenant.Id, asTracking: true, cancellationToken, plan.Value.Suite).ConfigureAwait(false);
        var preview = Compare(tenant, group.Value, rows, plan.Value, application, applied: true, tenant.EntitlementsRevision);
        if (preview.IsFailure || !preview.Value.ChangesAnything)
        {
            return preview;
        }

        var difference = preview.Value;
        int revision;
        if (group.Value.Suite is { } suite)
        {
            suite.ApplyPlan(plan.Value.Code, plan.Value.GraceDays);
            revision = tenant.NextRevision();
        }
        else
        {
            revision = tenant.ApplyPlan(plan.Value.Code, plan.Value.GraceDays);
        }

        var byKey = rows.ToDictionary(r => (r.Kind, r.Code));
        foreach (var added in difference.Added)
        {
            context.Entitlements.Add(new TenantEntitlement(tenant.Id, added.Kind, added.Code, added.After!, plan.Value.Suite));
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
            context.EntitlementHistory.Add(new EntitlementChange(tenant.Id, tenant.Slug, revision, EntitlementChange.PlanKind, HistoryCode(EntitlementChange.PlanKind, plan.Value.Suite), PlanValue(difference.PlanBefore), PlanValue(difference.PlanAfter), reason));
        }

        if (difference.GraceDaysBefore != difference.GraceDaysAfter)
        {
            context.EntitlementHistory.Add(new EntitlementChange(tenant.Id, tenant.Slug, revision, EntitlementChange.GraceKind, HistoryCode(EntitlementChange.GraceKind, plan.Value.Suite), Serialize(new { days = difference.GraceDaysBefore }), Serialize(new { days = difference.GraceDaysAfter }), reason));
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
            ["suite"] = plan.Value.Suite,
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

    /// <summary>
    /// A suite the tenant gains, with its plan, inside the caller's transaction (TF-244 AC3): the suite's row, then its
    /// plan applied to the suite's group alone. The plan must be the suite's.
    /// </summary>
    public async Task<Result<PlanPreview>> AddSuiteWithinAsync(Tenant tenant, string suite, PlanApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(application);
        var plan = await FindPlanAsync(application.Plan, cancellationToken).ConfigureAwait(false);
        if (plan.IsFailure)
        {
            return plan.Error;
        }

        if (plan.Value.Suite != suite)
        {
            return Error.Validation(TenancyErrors.PlanOtherGroup, $"The plan '{plan.Value.Code}' is a plan of {UnitCatalogue.GroupName(plan.Value.Suite)}, not of {UnitCatalogue.GroupName(suite)}.", new FieldError("plan", "other_group", "Name a plan of the suite."));
        }

        if (await context.Suites.AnyAsync(s => s.TenantId == tenant.Id && s.Suite == suite, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(TenancyErrors.SuiteInstalled, $"Tenant '{tenant.Slug}' has the suite '{suite}' already; apply another of its plans to change what it has.");
        }

        context.Suites.Add(new TenantSuite(tenant.Id, suite));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ApplyWithinAsync(tenant, application, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A suite the tenant loses, inside the caller's transaction (TF-244 AC4): the suite's row and its group's entitlements
    /// go, each change in the history with the operator's reason, so the unit switches the suite's modules off within a
    /// minute. Nothing of the suite's data is touched: it is in the tenant's own database, and stays there.
    /// </summary>
    public async Task<Result<PlanPreview>> RemoveSuiteWithinAsync(Tenant tenant, string suite, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var row = await context.Suites.SingleOrDefaultAsync(s => s.TenantId == tenant.Id && s.Suite == suite, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Error.NotFound(TenancyErrors.SuiteNotInstalled, $"Tenant '{tenant.Slug}' does not have the suite '{suite}'.");
        }

        var rows = await RowsAsync(tenant.Id, asTracking: true, cancellationToken, suite).ConfigureAwait(false);
        var revision = tenant.NextRevision();
        var removed = rows.Select(r => new EntitlementDifference(r.Kind, r.Code, r.Value, null)).ToList();
        context.EntitlementHistory.Add(new EntitlementChange(tenant.Id, tenant.Slug, revision, EntitlementChange.PlanKind, HistoryCode(EntitlementChange.PlanKind, suite), PlanValue(row.Plan), null, reason));
        foreach (var entitlement in rows)
        {
            context.EntitlementHistory.Add(new EntitlementChange(tenant.Id, tenant.Slug, revision, KindName(entitlement.Kind), entitlement.Code, Serialize(entitlement.Value), null, reason));
            context.Entitlements.Remove(entitlement);
        }

        context.Suites.Remove(row);
        await flagPublications.TouchAsync(Iron.Kernel.Flags.Contracts.FlagRulesDocument.TenantPath(tenant.Slug), cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var warnings = removed.Where(r => r.Kind == EntitlementKind.Module).Select(r => $"The module '{r.Code}' is switched off: its data stays in the tenant's database and is found again when the suite is added back.").ToList();
        return new PlanPreview(tenant.Slug, row.Plan, string.Empty, row.GraceDays, 0, Applied: true, revision, [], Order(removed), [], warnings, suite);
    }

    /// <summary>
    /// The plan a suite is added with: the one named, or the suite's only plan. A suite this build does not carry is
    /// refused, and so is a suite with several plans when none is named.
    /// </summary>
    public async Task<Result<string>> SuitePlanAsync(string suite, string? plan, CancellationToken cancellationToken)
    {
        if (suite == SuiteName.Foundation || !catalogue.KnowsGroup(suite))
        {
            var carried = catalogue.Catalogue.Suites.Where(s => s != SuiteName.Foundation).ToList();
            return Error.Validation(TenancyErrors.SuiteUnknown, $"'{suite}' is not a suite this build carries; it carries {(carried.Count == 0 ? "none" : string.Join(", ", carried))}.", new FieldError("suite", "unknown", "Name a suite this build carries."));
        }

        if (!string.IsNullOrWhiteSpace(plan))
        {
            return plan.Trim().ToLowerInvariant();
        }

        var plans = await context.Plans.AsNoTracking().Where(p => p.Suite == suite).OrderBy(p => p.Code).Select(p => p.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        return plans.Count == 1
            ? plans[0]
            : Error.Validation(TenancyErrors.SuitePlanRequired, plans.Count == 0 ? $"The suite '{suite}' has no plan yet: save one first." : $"Name the plan of the suite '{suite}': {string.Join(", ", plans)}.", new FieldError("plan", "required", "Name one of the suite's plans."));
    }

    /// <summary>The suites a tenant has, with the plan it holds for each, by short name.</summary>
    public async Task<IReadOnlyList<TenantSuite>> SuitesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await context.Suites.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.Suite).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The effective entitlements of a tenant as the store keeps them for its unit (ADR-0034): the union of its plans'
    /// entitlements, each entry with its group and that group's grace days, and the suites with their plans (TF-244).
    /// </summary>
    public async Task<EntitlementsDocument?> DocumentAsync(string slug, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return null;
        }

        var rows = await RowsAsync(tenant.Id, asTracking: false, cancellationToken).ConfigureAwait(false);
        var suites = await SuitesAsync(tenant.Id, cancellationToken).ConfigureAwait(false);
        var grace = suites.ToDictionary(s => s.Suite, s => s.GraceDays, StringComparer.Ordinal);
        int? GraceOf(TenantEntitlement row) => row.Suite == SuiteName.Foundation ? tenant.GraceDays : grace.TryGetValue(row.Suite, out var days) ? days : null;
        var modules = rows.Where(r => r.Kind == EntitlementKind.Module)
            .Select(r => new ModuleEntitlement(r.Code, r.Status, r.SubscriptionType ?? SubscriptionType.Platform, r.UserLimit, r.StartsOn, r.EndsOn, GraceOf(r), r.Suite))
            .ToList();
        var features = rows.Where(r => r.Kind == EntitlementKind.Feature)
            .Select(r => new FeatureEntitlement(r.Code, r.Status, r.StartsOn, r.EndsOn, GraceOf(r), r.Suite))
            .ToList();
        var plans = suites.Select(s => new SuitePlan(s.Suite, s.Plan, s.GraceDays)).ToList();
        return new EntitlementsDocument(EntitlementsDocument.CurrentFormat, tenant.Slug, tenant.EntitlementsRevision, tenant.Plan, tenant.GraceDays, time.GetUtcNow(), modules, features, plans);
    }

    /// <summary>The tenant's entitlements, all of them or one group's (the Foundation's or a suite's).</summary>
    public async Task<IReadOnlyList<TenantEntitlement>> RowsAsync(Guid tenantId, bool asTracking, CancellationToken cancellationToken, string? suite = null)
    {
        var query = context.Entitlements.Where(e => e.TenantId == tenantId);
        if (suite is not null)
        {
            query = query.Where(e => e.Suite == suite);
        }

        if (!asTracking)
        {
            query = query.AsNoTracking();
        }

        return await query.OrderBy(e => e.Kind).ThenBy(e => e.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string KindName(EntitlementKind kind) => kind == EntitlementKind.Module ? "module" : "feature";

    public static string? Serialize(object? value) => value is null ? null : JsonSerializer.Serialize(value, Json);

    /// <summary>The history's code of a plan or grace change: the Foundation's keeps the kind as its code, a suite's names the suite.</summary>
    private static string HistoryCode(string kind, string suite) => suite == SuiteName.Foundation ? kind : suite;

    /// <summary>A plan in the history: its code, or nothing before a suite's first plan and after its removal.</summary>
    private static string? PlanValue(string plan) => string.IsNullOrEmpty(plan) ? null : Serialize(new { code = plan });

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
        if (unknown.Count > 0)
        {
            return Error.Validation(TenancyErrors.PlanUnknownEntitlements, $"The plan '{plan.Code}' names what this build cannot license: {string.Join(" ", unknown)} Change the plan first.", unknown.Select(problem => new FieldError("plan", "unknown", problem)).ToList());
        }

        // TF-244 AC7, when applied as when saved: a plan names only its own group's modules and features.
        var otherGroup = catalogue.OtherGroup(plan);
        return otherGroup.Count > 0
            ? Error.Validation(TenancyErrors.PlanOtherGroup, $"The plan '{plan.Code}' of {UnitCatalogue.GroupName(plan.Suite)} names what belongs to another group: {string.Join(" ", otherGroup)} Change the plan first.", otherGroup.Select(problem => new FieldError("plan", "other_group", problem)).ToList())
            : plan;
    }

    /// <summary>The plan the tenant holds now for the plan's group, and its grace days; for a suite, its row, which must exist.</summary>
    private async Task<Result<GroupState>> GroupAsync(Tenant tenant, string suite, bool tracking, CancellationToken cancellationToken)
    {
        if (suite == SuiteName.Foundation)
        {
            return new GroupState(tenant.Plan, tenant.GraceDays, null);
        }

        var query = context.Suites.Where(s => s.TenantId == tenant.Id && s.Suite == suite);
        var row = await (tracking ? query : query.AsNoTracking()).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null
            ? Error.Conflict(TenancyErrors.SuiteNotInstalled, $"Tenant '{tenant.Slug}' does not have the suite '{suite}': add it first (make suite-add), which applies the plan you name.")
            : new GroupState(row.Plan, row.GraceDays, row);
    }

    private Result<PlanPreview> Compare(Tenant tenant, GroupState group, IReadOnlyList<TenantEntitlement> rows, Plan plan, PlanApplication application, bool applied, int revision)
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

        return new PlanPreview(tenant.Slug, group.Plan, plan.Code, group.GraceDays, plan.GraceDays, applied, revision, Order(added), Order(removed), Order(changed), warnings, plan.Suite);
    }

    /// <summary>A new entitlement starts on the date asked for, or today; one the tenant has keeps its start unless a date is asked for.</summary>
    private DateOnly StartOf(Dictionary<(EntitlementKind, string), TenantEntitlement> existing, EntitlementKind kind, string code, PlanApplication application) =>
        application.StartsOn ?? (existing.TryGetValue((kind, code), out var row) ? row.StartsOn : Today);

    private static List<EntitlementDifference> Order(List<EntitlementDifference> differences) =>
        [.. differences.OrderBy(d => d.Kind).ThenBy(d => d.Code, StringComparer.Ordinal)];

    /// <summary>A group's plan as the tenant holds it now; <see cref="Suite"/> is the suite's row, null for the Foundation.</summary>
    private sealed record GroupState(string Plan, int GraceDays, TenantSuite? Suite);
}
