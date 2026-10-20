using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data.Lists;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Authorization.Api;

/// <summary>A side of a rule as the API writes it: its kind, its value, and its name in words (the permission's description, or the role's name).</summary>
public sealed record ConflictSideResponse(string Kind, string Value, string Name);

public sealed record ConflictRuleResponse(Guid Id, string Name, ConflictSideResponse First, ConflictSideResponse Second, string Mode, string Reason, Guid OwnerId, string? OwnerName, DateTimeOffset UpdatedAt, int Version);

public sealed record ConflictSideRequest(string Kind, string Value);

public sealed record ConflictRuleRequest(string Name, ConflictSideRequest First, ConflictSideRequest Second, string Mode, string? Reason, Guid? OwnerId);

public sealed class ConflictRuleRequestValidator : AbstractValidator<ConflictRuleRequest>
{
    public ConflictRuleRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(ConflictRule.NameMaxLength);
        RuleFor(r => r.First).NotNull();
        RuleFor(r => r.Second).NotNull();
        RuleFor(r => r.First.Kind).Must(ConflictSides.IsKnown).When(r => r.First is not null).WithMessage("Choose a permission or a role.");
        RuleFor(r => r.Second.Kind).Must(ConflictSides.IsKnown).When(r => r.Second is not null).WithMessage("Choose a permission or a role.");
        RuleFor(r => r.First.Value).NotEmpty().When(r => r.First is not null);
        RuleFor(r => r.Second.Value).NotEmpty().When(r => r.Second is not null);
        RuleFor(r => r.Mode).Must(ConflictModes.IsKnown).WithMessage("Choose block or warn.");
        RuleFor(r => r.Reason).MaximumLength(ConflictRule.ReasonMaxLength);
    }
}

/// <summary>One grant of a conflict: the role, where it applies, and whether it is the person's own or delegated to them.</summary>
public sealed record ConflictGrantResponse(Guid? AssignmentId, Guid? DelegationId, string Role, string ScopeKind, Guid? UnitId, string? UnitName, string Source);

/// <summary>One conflict of the report: the person, the rule, the two grants, and the unit both reach (null for the whole tenant).</summary>
public sealed record ConflictEntryResponse(Guid Id, Guid RuleId, string RuleName, string Mode, Guid PrincipalId, string PrincipalName, ConflictGrantResponse First, ConflictGrantResponse Second, Guid? UnitId, string? UnitName, DateTimeOffset ComputedAt)
{
    public static ConflictEntryResponse From(ConflictReportEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ConflictEntryResponse(
            entry.Id,
            entry.RuleId,
            entry.RuleName,
            entry.Mode,
            entry.PrincipalId,
            entry.PrincipalName,
            new ConflictGrantResponse(entry.FirstAssignmentId, entry.FirstDelegationId, entry.FirstRole, entry.FirstScopeKind, entry.FirstUnitId, entry.FirstUnitName, entry.FirstSource),
            new ConflictGrantResponse(entry.SecondAssignmentId, entry.SecondDelegationId, entry.SecondRole, entry.SecondScopeKind, entry.SecondUnitId, entry.SecondUnitName, entry.SecondSource),
            entry.UnitId,
            entry.UnitName,
            entry.ComputedAt);
    }
}

/// <summary>A run of the report; none before the first.</summary>
public sealed record ConflictRunResponse(Guid Id, string Trigger, int Rules, int Conflicts, DateTimeOffset ComputedAt);

public sealed record LatestRunResponse(ConflictRunResponse? Run);

/// <summary>A conflict a grant would create, for the grant dialog before it is confirmed: the rule, and the grant on the other side.</summary>
public sealed record GrantConflictResponse(Guid RuleId, string RuleName, string Mode, string Reason, ConflictGrantResponse With, Guid? UnitId, string? UnitName);

public sealed record GrantConflictsResponse(IReadOnlyList<GrantConflictResponse> Data);

/// <summary>
/// Segregation of duties (TF-036, ADR-0039): the conflict rules, read with <c>authz.sod.read</c> and written with
/// <c>authz.sod.manage</c>, tenant-wide; the report of existing conflicts and its run on demand; and the conflicts a
/// grant would create, for the grant dialog, with the grant's own permission.
/// </summary>
internal static class ConflictRulesEndpoints
{
    public static readonly ListFields<ConflictRule> RuleFields = new ListFields<ConflictRule>()
        .Add("id", r => r.Id, FieldUse.Select | FieldUse.Filter)
        .Add("name", r => r.Name)
        .Add("mode", r => r.Mode)
        .Add("updated_at", r => r.UpdatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("name");

    public static readonly ListFields<ConflictReportEntry> EntryFields = new ListFields<ConflictReportEntry>()
        .Add("id", e => e.Id, FieldUse.Select | FieldUse.Filter)
        .Add("rule_id", e => e.RuleId, FieldUse.Select | FieldUse.Filter)
        .Add("rule_name", e => e.RuleName)
        .Add("mode", e => e.Mode)
        .Add("principal_id", e => e.PrincipalId, FieldUse.Select | FieldUse.Filter)
        .Add("principal_name", e => e.PrincipalName)
        .Add("unit_name", e => e.UnitName)
        .Add("computed_at", e => e.ComputedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("principal_name");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var rules = endpoints.MapApiGroup("conflict-rules");
        rules.MapGet("/", ListRulesAsync).RequirePermission(AuthorizationPermissions.SodRead).WithName("ListConflictRules").WithSummary("The conflict rules of this tenant.");
        rules.MapPost("/", CreateAsync).RequirePermission(AuthorizationPermissions.SodManage).Validate<ConflictRuleRequest>().WithName("CreateConflictRule").WithSummary("Define a conflict rule between two permissions or roles, to block or warn.");
        rules.MapPut("/{id:guid}", UpdateAsync).RequirePermission(AuthorizationPermissions.SodManage).Validate<ConflictRuleRequest>().WithName("UpdateConflictRule").WithSummary("Change a conflict rule.");
        rules.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(AuthorizationPermissions.SodManage).WithName("DeleteConflictRule").WithSummary("Remove a conflict rule.");

        var report = endpoints.MapApiGroup("conflict-report");
        report.MapGet("/", ListEntriesAsync).RequirePermission(AuthorizationPermissions.SodRead).WithName("ListConflicts").WithSummary("The conflicts that exist, by person, rule, the two grants that cause each, and the unit concerned.");
        report.MapGet("/runs/latest", LatestRunAsync).RequirePermission(AuthorizationPermissions.SodRead).WithName("GetLatestConflictRun").WithSummary("When the report last ran in full, and what it found.");
        report.MapPost("/runs", RunAsync).RequirePermission(AuthorizationPermissions.SodManage).WithName("RunConflictReport").WithSummary("Compute the report now, for everyone.");

        endpoints.MapApiGroup("role-assignments").MapPost("/conflicts", GrantConflictsAsync)
            .RequirePermission(AuthorizationPermissions.AssignmentsManage)
            .ScopedByRow()
            .Validate<GrantRoleRequest>()
            .WithName("PreviewGrantConflicts")
            .WithSummary("The conflicts granting this role at this scope would create, before the grant: block rules refuse it, warn rules list it.");
    }

    private static async Task<IResult> ListRulesAsync(HttpContext http, AuthorizationDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, RuleFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.ConflictRules.AsNoTracking().ApplyListQuery(query, RuleFields).ToPageAsync(query.Page, cancellationToken);
        var described = (await DescribeAsync(http, context, page.Items, cancellationToken)).ToDictionary(r => r.Id);
        return ApiResults.List(page.Map(r => described[r.Id]), http, query);
    }

    private static async Task<IResult> CreateAsync(ConflictRuleRequest request, HttpContext http, ConflictService conflicts, AuthorizationDbContext context, CancellationToken cancellationToken)
    {
        var result = await conflicts.CreateAsync(Input(request), cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        var described = (await DescribeAsync(http, context, [result.Value], cancellationToken))[0];
        return TypedResults.Created($"{ApiRouting.Prefix}/conflict-rules/{described.Id}", described);
    }

    private static async Task<IResult> UpdateAsync(Guid id, ConflictRuleRequest request, HttpContext http, ConflictService conflicts, AuthorizationDbContext context, CancellationToken cancellationToken)
    {
        var result = await conflicts.UpdateAsync(id, Input(request), cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.Ok((await DescribeAsync(http, context, [result.Value], cancellationToken))[0]);
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, ConflictService conflicts, CancellationToken cancellationToken)
    {
        var result = await conflicts.DeleteAsync(id, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : TypedResults.NoContent();
    }

    private static async Task<IResult> ListEntriesAsync(HttpContext http, AuthorizationDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, EntryFields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.ConflictReportEntries.AsNoTracking().ApplyListQuery(query, EntryFields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(ConflictEntryResponse.From), http, query);
    }

    private static async Task<IResult> LatestRunAsync(AuthorizationDbContext context, CancellationToken cancellationToken)
    {
        var run = await context.ConflictReportRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return TypedResults.Ok(new LatestRunResponse(run is null ? null : Run(run)));
    }

    private static async Task<IResult> RunAsync(ConflictService conflicts, CancellationToken cancellationToken) =>
        TypedResults.Ok(Run(await conflicts.RunReportAsync(ConflictReportTriggers.OnDemand, cancellationToken)));

    private static async Task<IResult> GrantConflictsAsync(GrantRoleRequest request, HttpContext http, ConflictService conflicts, AuthorizationDbContext context, CancellationToken cancellationToken)
    {
        var role = await context.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.RoleId, cancellationToken);
        if (role is null)
        {
            return ApiResults.Problem(Error.Validation(AuthorizationErrors.RoleNotFound, "There is no role with this id.", new FieldError("role_id", "not_found", "Choose an existing role.")), http);
        }

        var found = await conflicts.ForGrantAsync(request.PrincipalId, role, request.ToScope(), cancellationToken);
        var unitIds = found.SelectMany(c => new[] { c.UnitId, Other(c).UnitId }).OfType<Guid>().Distinct().ToArray();
        var units = await context.OrganisationUnits.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);
        return TypedResults.Ok(new GrantConflictsResponse([.. found.Select(c =>
        {
            var other = Other(c);
            var with = new ConflictGrantResponse(other.AssignmentId, other.DelegationId, other.RoleName, Scopes.NameOf(other.Kind), other.UnitId, other.UnitId is { } unit ? units.GetValueOrDefault(unit) : null, other.DelegationId is null ? ConflictSources.Assignment : ConflictSources.Delegation);
            return new GrantConflictResponse(c.Rule.Id, c.Rule.Name, c.Rule.Mode, c.Rule.Reason, with, c.UnitId, c.UnitId is { } at ? units.GetValueOrDefault(at) : null);
        })]));
    }

    /// <summary>The grant a conflict meets on the other side of the new one; the new one itself when it holds both sides.</summary>
    private static HeldGrant Other(Conflict conflict) => conflict.First.IsNew ? conflict.Second : conflict.First;

    private static ConflictRuleInput Input(ConflictRuleRequest request) =>
        new(request.Name, new ConflictSide(request.First.Kind, request.First.Value), new ConflictSide(request.Second.Kind, request.Second.Value), request.Mode, request.Reason, request.OwnerId);

    private static ConflictRunResponse Run(ConflictReportRun run) => new(run.Id, run.Trigger, run.Rules, run.Conflicts, run.CreatedAt);

    /// <summary>The rules with their sides named in words: a permission by its description, a role by its name.</summary>
    private static async Task<IReadOnlyList<ConflictRuleResponse>> DescribeAsync(HttpContext http, AuthorizationDbContext context, IReadOnlyList<ConflictRule> rules, CancellationToken cancellationToken)
    {
        var catalogue = http.RequestServices.GetRequiredService<PermissionCatalogue>();
        var principals = http.RequestServices.GetService<IPrincipals>();
        var roleIds = rules.SelectMany(r => new[] { (r.FirstKind, r.FirstValue), (r.SecondKind, r.SecondValue) })
            .Where(s => s.Item1 == ConflictSides.Role && Guid.TryParse(s.Item2, out _))
            .Select(s => Guid.Parse(s.Item2))
            .Distinct()
            .ToArray();
        var roles = await context.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        var owners = principals is null ? new Dictionary<Guid, PrincipalSummary>() : await principals.FindAsync([.. rules.Select(r => r.OwnerId).Distinct()], cancellationToken);
        ConflictSideResponse Side(string kind, string value) => new(
            kind,
            value,
            kind == ConflictSides.Permission
                ? catalogue.Find(value)?.Description ?? value
                : Guid.TryParse(value, out var id) && roles.TryGetValue(id, out var name) ? name : value);
        return [.. rules.Select(r => new ConflictRuleResponse(r.Id, r.Name, Side(r.FirstKind, r.FirstValue), Side(r.SecondKind, r.SecondValue), r.Mode, r.Reason, r.OwnerId, owners.GetValueOrDefault(r.OwnerId)?.DisplayName, r.UpdatedAt, r.Version))];
    }
}
