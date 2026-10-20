using FluentValidation;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
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

namespace Iron.Kernel.Authorization.Api;

/// <summary>What a client sees of a role. The code identifies it; the name is for people (ADR-0031).</summary>
public sealed record RoleResponse(
    Guid Id,
    string Code,
    string Name,
    string Description,
    bool IsSystem,
    PrincipalKind PrincipalType,
    IReadOnlyList<string> Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    public static RoleResponse From(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return new RoleResponse(role.Id, role.Code, role.Name, role.Description, role.IsSystem, role.PrincipalType, role.Permissions, role.CreatedAt, role.UpdatedAt, role.Version);
    }
}

public sealed record CreateRoleRequest(string Code, string Name, string? Description, PrincipalKind PrincipalType, IReadOnlyList<string> Permissions);

public sealed record UpdateRoleRequest(string Name, string? Description, PrincipalKind PrincipalType, IReadOnlyList<string> Permissions);

public sealed record ImportRolesRequest(bool DryRun, RoleExport Document);

/// <summary>One permission of the catalogue as the role editor shows it (TF-030 AC2): grouped by module, with its risk.</summary>
public sealed record PermissionResponse(string Name, string Module, string Description, Risk Risk);

public sealed record PermissionsResponse(IReadOnlyList<PermissionResponse> Data);

public sealed class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    public CreateRoleRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().MaximumLength(Role.CodeMaxLength).Must(code => Role.IsValidCode(code?.Trim().ToLowerInvariant())).WithErrorCode("invalid").WithMessage("Use lowercase letters, digits and hyphens, starting with a letter.");
        RuleFor(r => r.Name).NotEmpty().MaximumLength(Role.NameMaxLength);
        RuleFor(r => r.Description).MaximumLength(Role.DescriptionMaxLength);
        RuleFor(r => r.Permissions).NotNull();
    }
}

public sealed class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    public UpdateRoleRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(Role.NameMaxLength);
        RuleFor(r => r.Description).MaximumLength(Role.DescriptionMaxLength);
        RuleFor(r => r.Permissions).NotNull();
    }
}

public sealed class ImportRolesRequestValidator : AbstractValidator<ImportRolesRequest>
{
    public ImportRolesRequestValidator()
    {
        RuleFor(r => r.Document).NotNull();
    }
}

/// <summary>
/// Roles (TF-032, ADR-0031): read at any scope, because a unit administrator must see what they may grant; changed only
/// tenant-wide. The permission catalogue for the editor; export and import between tenants with a dry run first.
/// </summary>
internal static class RolesEndpoints
{
    /// <summary>The whitelist of the list grammar (TF-093): what can be selected, filtered and sorted.</summary>
    public static readonly ListFields<Role> Fields = new ListFields<Role>()
        .Add("id", r => r.Id, FieldUse.Select | FieldUse.Filter)
        .Add("code", r => r.Code)
        .Add("name", r => r.Name)
        .Add("description", r => r.Description, FieldUse.Select)
        .Add("is_system", r => r.IsSystem, FieldUse.Select | FieldUse.Filter)
        .Add("principal_type", r => r.PrincipalType, FieldUse.Select | FieldUse.Filter)
        .Add("permissions", r => r.Permissions, FieldUse.Select)
        .Add("created_at", r => r.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .Add("updated_at", r => r.UpdatedAt, FieldUse.Select | FieldUse.Sort)
        .Add("version", r => r.Version, FieldUse.Select)
        .SortByDefault("code");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var roles = endpoints.MapApiGroup("roles");
        roles.MapGet("/", ListAsync).RequirePermission(AuthorizationPermissions.RolesRead).ScopedByRow().WithName("ListRoles").WithSummary("List the roles of this tenant.");
        // Export and import are an enterprise feature (ADR-0034): a tenant without it gets entitlements.feature_disabled.
        roles.MapGet("/export", ExportAsync).RequirePermission(AuthorizationPermissions.RolesRead).ScopedByRow().RequireFeature(AuthorizationFeatures.RoleTransfer.Code).WithName("ExportRoles").WithSummary("The tenant roles as a document another tenant can import.");
        roles.MapPost("/import", ImportAsync).RequirePermission(AuthorizationPermissions.RolesManage).RequireFeature(AuthorizationFeatures.RoleTransfer.Code).Validate<ImportRolesRequest>().WithName("ImportRoles").WithSummary("Compare a role export with this tenant's roles, and apply it unless it is a dry run.");
        roles.MapGet("/{id:guid}", GetAsync).RequirePermission(AuthorizationPermissions.RolesRead).ScopedByRow().WithName("GetRole").WithSummary("One role.");
        roles.MapPost("/", CreateAsync).RequirePermission(AuthorizationPermissions.RolesManage).Validate<CreateRoleRequest>().WithName("CreateRole").WithSummary("Create a tenant role.");
        roles.MapPut("/{id:guid}", UpdateAsync).RequirePermission(AuthorizationPermissions.RolesManage).Validate<UpdateRoleRequest>().WithName("UpdateRole").WithSummary("Change a tenant role's name, description, principal type and permissions.");
        roles.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(AuthorizationPermissions.RolesManage).WithName("DeleteRole").WithSummary("Delete a tenant role that is not assigned.");

        var permissions = endpoints.MapApiGroup("permissions");
        permissions.MapGet("/", Catalogue).RequirePermission(AuthorizationPermissions.RolesRead).ScopedByRow().WithName("ListPermissions").WithSummary("Every permission of this host, for the role editor.");
    }

    private static async Task<IResult> ListAsync(HttpContext http, AuthorizationDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.Roles.AsNoTracking().ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(RoleResponse.From), http, query);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, RoleService roles, CancellationToken cancellationToken)
    {
        var role = await roles.FindAsync(id, cancellationToken);
        return role is null
            ? ApiResults.Problem(Error.NotFound(AuthorizationErrors.RoleNotFound, "There is no role with this id."), http)
            : TypedResults.Ok(RoleResponse.From(role));
    }

    private static async Task<IResult> CreateAsync(CreateRoleRequest request, HttpContext http, RoleService roles, CancellationToken cancellationToken)
    {
        var result = await roles.CreateAsync(request.Code, new RoleInput(request.Name, request.Description ?? string.Empty, request.PrincipalType, request.Permissions), cancellationToken);
        return ApiResults.From(result, http, role => TypedResults.Created($"{ApiRouting.Prefix}/roles/{role.Id}", RoleResponse.From(role)));
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateRoleRequest request, HttpContext http, RoleService roles, CancellationToken cancellationToken)
    {
        var result = await roles.UpdateAsync(id, new RoleInput(request.Name, request.Description ?? string.Empty, request.PrincipalType, request.Permissions), cancellationToken);
        return ApiResults.From(result, http, role => TypedResults.Ok(RoleResponse.From(role)));
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, RoleService roles, CancellationToken cancellationToken)
    {
        var result = await roles.DeleteAsync(id, cancellationToken);
        return ApiResults.From(result, http, () => TypedResults.NoContent());
    }

    private static async Task<IResult> ExportAsync(RoleService roles, CancellationToken cancellationToken) => TypedResults.Ok(await roles.ExportAsync(cancellationToken));

    private static async Task<IResult> ImportAsync(ImportRolesRequest request, HttpContext http, RoleService roles, CancellationToken cancellationToken)
    {
        var result = await roles.ImportAsync(request.Document, request.DryRun, cancellationToken);
        return ApiResults.From(result, http, diff => TypedResults.Ok(diff));
    }

    private static IResult Catalogue(PermissionCatalogue catalogue) =>
        TypedResults.Ok(new PermissionsResponse(catalogue.All.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new PermissionResponse(p.Name, p.Module, p.Description, p.Risk)).ToList()));
}
