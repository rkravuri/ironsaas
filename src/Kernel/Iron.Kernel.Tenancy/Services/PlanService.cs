using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Seeding;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>A licensed module as a plan names it; how it is sold comes from the catalogue.</summary>
public sealed record PlanModuleInput(string Code, EntitlementStatus Status, int? UserLimit);

/// <summary>What a plan holds when it is saved: its group (the Foundation unless a suite is named, TF-244) and its entitlements.</summary>
public sealed record PlanInput(string Name, int GraceDays, IReadOnlyList<PlanModuleInput> Modules, IReadOnlyList<string> Features, string? Suite = null);

/// <summary>
/// Plans (TF-042): templates of licensed modules, features and grace days. A plan is saved only with codes the
/// catalogue of this build knows (TF-042 AC3); a kernel module is never in one (ADR-0034). A plan belongs to the
/// Foundation or to one suite, names only its group's modules and features (TF-244 AC7), and keeps its group.
/// </summary>
public sealed class PlanService(TenancyDbContext context, UnitCatalogue catalogue, IUnitOfWork unitOfWork, IAuditWriter audit, ICurrentPrincipal current)
{
    public Task<Plan?> FindAsync(string code, CancellationToken cancellationToken)
    {
        var normalised = code.Trim().ToLowerInvariant();
        return context.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Code == normalised, cancellationToken);
    }

    /// <summary>Creates the plan, or replaces what an existing one holds; the tenants it was applied to keep theirs until it is applied again.</summary>
    public async Task<Result<Plan>> SaveAsync(string code, PlanInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalised = code.Trim().ToLowerInvariant();
        if (!Plan.IsValidCode(normalised))
        {
            return Error.Validation(TenancyErrors.PlanCodeInvalid, $"'{code}' is not a plan code: lowercase letters, digits and hyphens, starting with a letter.", new FieldError("code", "invalid", "Use lowercase letters, digits and hyphens."));
        }

        var unknown = catalogue.Unknown(input.Modules.Select(m => m.Code), input.Features);
        if (unknown.Count > 0)
        {
            return Error.Validation(TenancyErrors.PlanUnknownEntitlements, $"The plan names what this build cannot license: {string.Join(" ", unknown)}", unknown.Select(problem => new FieldError("plan", "unknown", problem)).ToList());
        }

        var existing = await context.Plans.AsNoTracking().Where(p => p.Code == normalised).Select(p => p.Suite).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var suite = (input.Suite ?? existing ?? SuiteName.Foundation).Trim().ToLowerInvariant();
        if (!catalogue.KnowsGroup(suite))
        {
            return Error.Validation(TenancyErrors.PlanSuiteUnknown, $"'{suite}' is neither the Foundation nor a suite this build carries ({string.Join(", ", catalogue.Catalogue.Suites)}).", new FieldError("suite", "unknown", "Name the Foundation or a suite of this build."));
        }

        if (existing is not null && existing != suite)
        {
            return Error.Validation(TenancyErrors.PlanSuiteFixed, $"The plan '{normalised}' is a plan of {UnitCatalogue.GroupName(existing)}; a plan stays in its group, so make a new plan for {UnitCatalogue.GroupName(suite)}.", new FieldError("suite", "fixed", "A plan keeps its group."));
        }

        var otherGroup = catalogue.OtherGroup(suite, input.Modules.Select(m => m.Code), input.Features);
        if (otherGroup.Count > 0)
        {
            return Error.Validation(TenancyErrors.PlanOtherGroup, $"The plan '{normalised}' of {UnitCatalogue.GroupName(suite)} names what belongs to another group: {string.Join(" ", otherGroup)}", otherGroup.Select(problem => new FieldError("plan", "other_group", problem)).ToList());
        }

        var modules = new List<PlanModule>();
        foreach (var module in input.Modules)
        {
            var declared = catalogue.Catalogue.FindModule(module.Code)!;
            if (declared.SubscriptionType == SubscriptionType.PerPerson && module.UserLimit is null or < 0)
            {
                return Error.Validation("tenancy.plan.user_limit", $"The module '{module.Code}' is licensed per person: give the number of seats.", new FieldError("modules", "user_limit", $"Give '{module.Code}' a number of seats, 0 or more."));
            }

            modules.Add(new PlanModule(module.Code, module.Status, declared.SubscriptionType, declared.SubscriptionType == SubscriptionType.PerPerson ? module.UserLimit : null));
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var plan = await context.Plans.SingleOrDefaultAsync(p => p.Code == normalised, cancellationToken).ConfigureAwait(false);
        var before = plan is null ? null : Describe(plan);
        if (plan is null)
        {
            plan = new Plan(normalised, input.Name, input.GraceDays, modules, input.Features, suite);
            context.Plans.Add(plan);
        }
        else
        {
            plan.Change(input.Name, input.GraceDays, modules, input.Features);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["after"] = Describe(plan) };
        if (before is not null)
        {
            details["before"] = before;
        }

        await audit.WriteAsync(new AuditEvent(TenancyAuditEvents.PlanSaved, AuditOutcome.Success, current.Id, plan.Code, details, "plan", plan.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }

    private static string Describe(Plan plan) => $"suite {plan.Suite}; grace {plan.GraceDays}; modules {string.Join(",", plan.Modules.Select(m => $"{m.Code}:{m.Status.ToString().ToLowerInvariant()}:{m.UserLimit}"))}; features {string.Join(",", plan.Features)}";
}

/// <summary>
/// The control plane's plans: the two Foundation plans (A4), Standard without role export and import and Enterprise with
/// them, and the starter plans each suite brings in the catalogue (TF-244, ADR-0043 decision 8), such as the eProcurement
/// Suite's Essentials. Created once; a plan that exists is the operator's and is left as it is.
/// </summary>
public sealed class PlanSeeder(TenancyDbContext context, UnitCatalogue catalogue, ILogger<PlanSeeder> logger) : ISeeder
{
    public const string Standard = "standard";
    public const string Enterprise = "enterprise";
    public const string RoleTransfer = "authorization.role_transfer";

    public int Order => 300;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(new Plan(Standard, "Standard", 14, [], []), cancellationToken).ConfigureAwait(false);
        await EnsureAsync(new Plan(Enterprise, "Enterprise", 30, [], [RoleTransfer]), cancellationToken).ConfigureAwait(false);
        foreach (var starter in catalogue.Catalogue.Plans)
        {
            // Every module of a starter plan is enabled; a per-person module starts with no seats, which the operator sets.
            var modules = starter.Modules.Select(code => catalogue.Catalogue.FindModule(code) is { } module
                ? new PlanModule(code, EntitlementStatus.Enabled, module.SubscriptionType, module.SubscriptionType == SubscriptionType.PerPerson ? 0 : null)
                : new PlanModule(code, EntitlementStatus.Enabled, SubscriptionType.Platform, null)).ToList();
            await EnsureAsync(new Plan(starter.Code, starter.Name, starter.GraceDays, modules, starter.Features, starter.Suite), cancellationToken).ConfigureAwait(false);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAsync(Plan plan, CancellationToken cancellationToken)
    {
        var unknown = catalogue.Unknown(plan).Concat(catalogue.OtherGroup(plan)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException($"The seeded plan '{plan.Code}' names what this build cannot license, or another group's: {string.Join(" ", unknown)} Run make catalogue.");
        }

        if (!await context.Plans.AnyAsync(p => p.Code == plan.Code, cancellationToken).ConfigureAwait(false))
        {
            context.Plans.Add(plan);
            logger.LogInformation("Created the plan {Plan} with {Features} feature(s) and {GraceDays} grace days.", plan.Code, plan.Features.Count, plan.GraceDays);
        }
    }
}
