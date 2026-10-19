using System.Text.Json;
using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Flags.Contracts;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Tenancy.Api;

/// <summary>A flag of the catalogue as the operator sees it: its declaration, its status, and how many rules it has.</summary>
public sealed record FlagSummary(string Key, string Module, FlagType Type, FlagKind Kind, string Purpose, string Owner, DateOnly? RemoveBy, JsonElement Default, FlagStatus Status, int Rules);

public sealed record ReplaceFlagRulesRequest(int? Revision, string? Reason, IReadOnlyList<FlagRuleInput> Rules);

public sealed class ReplaceFlagRulesRequestValidator : AbstractValidator<ReplaceFlagRulesRequest>
{
    public ReplaceFlagRulesRequestValidator()
    {
        RuleFor(r => r.Rules).NotNull();
        RuleFor(r => r.Reason).MaximumLength(FlagRule.ReasonMaxLength);
    }
}

public sealed record KillSwitchRequest(bool On, string? Reason, string? Tenant);

public sealed class KillSwitchRequestValidator : AbstractValidator<KillSwitchRequest>
{
    public KillSwitchRequestValidator()
    {
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Say why, in words the tenants' administrators will read.").MaximumLength(FlagRule.ReasonMaxLength);
    }
}

/// <summary>A kill switch as the operator sees it: on for the whole platform or not, and the tenants it is on for.</summary>
public sealed record KillSwitchState(string Path, string Flag, string Purpose, bool On, DateTimeOffset? Since, string? Reason, IReadOnlyList<string> Tenants);

public sealed record FlagChangeResponse(Guid Id, int Revision, string Kind, JsonElement Before, JsonElement After, string? Reason, Guid OperatorId, string? OperatorName, DateTimeOffset ChangedAt);

/// <summary>
/// Feature flags in the control plane (TF-050 to TF-053, ADR-0035): the catalogue's flags, a flag's rules replaced
/// together with a revision check, their history, the kill switches, and the report. A kill switch's rules need
/// <c>tenancy.kill_switches.manage</c> besides <c>tenancy.flags.manage</c>, and a reason.
/// </summary>
internal static class FlagEndpoints
{
    public static readonly ListFields<FlagRuleChange> HistoryFields = new ListFields<FlagRuleChange>()
        .Add("id", c => c.Id, FieldUse.Select | FieldUse.Filter)
        .Add("revision", c => c.Revision, FieldUse.Select | FieldUse.Filter | FieldUse.Sort)
        .Add("kind", c => c.Kind, FieldUse.Select | FieldUse.Filter)
        .Add("operator_id", c => c.CreatedBy, FieldUse.Select | FieldUse.Filter)
        .Add("changed_at", c => c.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("changed_at");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var flags = endpoints.MapApiGroup("flags");
        flags.MapGet("/", ListAsync).RequirePermission(TenancyPermissions.FlagsRead).WithName("ListFlags").WithSummary("The flags of the catalogue with their declarations, status and rule counts.");
        flags.MapGet("/report", ReportAsync).RequirePermission(TenancyPermissions.FlagsRead).WithName("FlagReport").WithSummary("Flags past their removal date with their owner, and rules left for flags the catalogue no longer has.");
        flags.MapGet("/{key}/rules", RulesAsync).RequirePermission(TenancyPermissions.FlagsRead).WithName("GetFlagRules").WithSummary("A flag's rules in order, with their revision.");
        flags.MapPut("/{key}/rules", ReplaceAsync).RequirePermission(TenancyPermissions.FlagsManage).Validate<ReplaceFlagRulesRequest>().WithName("ReplaceFlagRules").WithSummary("Replace a flag's rules; a kill switch's need tenancy.kill_switches.manage and a reason.");
        flags.MapGet("/{key}/history", HistoryAsync).RequirePermission(TenancyPermissions.FlagsRead).WithName("GetFlagHistory").WithSummary("Every change of a flag's rules in order, with operator, before, after and reason.");

        var switches = endpoints.MapApiGroup("kill-switches");
        switches.MapGet("/", KillSwitchesAsync).RequirePermission(TenancyPermissions.FlagsRead).WithName("ListKillSwitches").WithSummary("Each kill switch: on for the platform or not, since when and why, and the tenants it is on for.");
        switches.MapPut("/{path}", FlipAsync).RequirePermission(TenancyPermissions.KillSwitchesManage).Validate<KillSwitchRequest>().WithName("FlipKillSwitch").WithSummary("Turn a kill switch on or off, for the platform or one tenant, with a reason.");
    }

    private static async Task<IResult> ListAsync(TenancyDbContext context, UnitCatalogue catalogue, CancellationToken cancellationToken)
    {
        var counts = await context.FlagRules.AsNoTracking().GroupBy(r => r.FlagKey).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        var flags = catalogue.Catalogue.Flags
            .Select(f => new FlagSummary(f.Key, f.Module, f.Type, f.Kind, f.Purpose, f.Owner, f.RemoveBy, f.Default, f.Status, counts.GetValueOrDefault(f.Key)))
            .ToList();
        return TypedResults.Ok(new { data = flags });
    }

    private static async Task<IResult> ReportAsync(FlagReportService report, CancellationToken cancellationToken) => TypedResults.Ok(await report.BuildAsync(cancellationToken));

    private static async Task<IResult> RulesAsync(string key, HttpContext http, FlagRuleService rules, CancellationToken cancellationToken) =>
        ApiResults.From(await rules.ListAsync(key, cancellationToken), http, list => TypedResults.Ok(list));

    private static async Task<IResult> ReplaceAsync(string key, ReplaceFlagRulesRequest request, HttpContext http, FlagRuleService rules, UnitCatalogue catalogue, FlagPublisher publisher, CancellationToken cancellationToken)
    {
        // TF-051 AC4: a kill switch's rules need the operator's own permission, checked here and denied like any other.
        if (catalogue.Catalogue.FindFlag(key) is { IsKillSwitch: true })
        {
            var refusal = await KillSwitchRefusalAsync(http, cancellationToken);
            if (refusal is not null)
            {
                return refusal;
            }
        }

        var result = await rules.ReplaceAsync(key, request.Rules, request.Revision, request.Reason, FlagRuleChange.RulesKind, cancellationToken);
        if (result.IsSuccess)
        {
            // After the commit: every path the change marked behind; the fleet reconciles what the store refuses.
            await publisher.ReconcileAsync(cancellationToken);
        }

        return ApiResults.From(result, http, list => TypedResults.Ok(list));
    }

    private static async Task<IResult> FlipAsync(string path, KillSwitchRequest request, HttpContext http, FlagRuleService rules, FlagPublisher publisher, CancellationToken cancellationToken)
    {
        var result = await rules.SetKillSwitchAsync(path, request.On, request.Reason, string.IsNullOrWhiteSpace(request.Tenant) ? null : request.Tenant.Trim(), cancellationToken);
        if (result.IsSuccess)
        {
            await publisher.ReconcileAsync(cancellationToken);
        }

        return ApiResults.From(result, http, list => TypedResults.Ok(list));
    }

    private static async Task<IResult> KillSwitchesAsync(TenancyDbContext context, UnitCatalogue catalogue, CancellationToken cancellationToken)
    {
        var switches = catalogue.Catalogue.Flags.Where(f => f.IsKillSwitch && f.Status == FlagStatus.Active).ToList();
        var keys = switches.Select(f => f.Key).ToList();
        var rules = await context.FlagRules.AsNoTracking().Where(r => keys.Contains(r.FlagKey) && r.ConditionKind == FlagConditionKind.Always).ToListAsync(cancellationToken);
        var states = switches.Select(flag =>
        {
            var platform = rules.FirstOrDefault(r => r.FlagKey == flag.Key && r.Scope == FlagScope.Global && r.ValueJson.Trim() == "true");
            var tenants = rules.Where(r => r.FlagKey == flag.Key && r.Scope == FlagScope.Tenant && r.ValueJson.Trim() == "true").Select(r => r.TenantSlug!).Order(StringComparer.Ordinal).ToList();
            return new KillSwitchState(flag.Key[Flag.KillSwitchPrefix.Length..], flag.Key, flag.Purpose, platform is not null, platform?.CreatedAt, platform?.Reason, tenants);
        }).ToList();
        return TypedResults.Ok(new { data = states });
    }

    private static async Task<IResult> HistoryAsync(string key, HttpContext http, TenancyDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, HistoryFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.FlagHistory.AsNoTracking().Where(c => c.FlagKey == key).ApplyListQuery(query, HistoryFields).ToPageAsync(query.Page, cancellationToken);
        var principals = http.RequestServices.GetService<IPrincipals>();
        IReadOnlyDictionary<Guid, PrincipalSummary> names = principals is null
            ? new Dictionary<Guid, PrincipalSummary>()
            : await principals.FindAsync([.. page.Items.Select(c => c.CreatedBy).Distinct()], cancellationToken);
        return ApiResults.List(page.Map(change => new FlagChangeResponse(change.Id, change.Revision, change.Kind, JsonSerializer.Deserialize<JsonElement>(change.Before), JsonSerializer.Deserialize<JsonElement>(change.After), change.Reason, change.CreatedBy, names.GetValueOrDefault(change.CreatedBy)?.DisplayName, change.CreatedAt)), http, query);
    }

    private static async Task<IResult?> KillSwitchRefusalAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var principal = http.RequestServices.GetRequiredService<ICurrentPrincipal>();
        var evaluator = http.RequestServices.GetRequiredService<IPermissionEvaluator>();
        var decision = await evaluator.EvaluateAsync(new AccessRequest(principal.Id, TenancyPermissions.KillSwitchesManage, PermissionFilter.Operation(http), http.Request.Path.Value ?? "/"), cancellationToken);
        if (decision.Allowed)
        {
            return null;
        }

        var problem = ProblemFactory.Create(Error.Forbidden(PermissionErrors.PermissionDenied, "Only the platform's operators turn kill switches on and off."), http);
        problem.Extensions["permission"] = TenancyPermissions.KillSwitchesManage.Name;
        return TypedResults.Problem(problem);
    }
}
