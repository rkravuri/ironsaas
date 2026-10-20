using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Authorization.Api;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Data.Seeding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Authorization;

/// <summary>
/// Permissions, roles, scoped assignments and the organisation tree (TF-030 to TF-034), delegations of duties and acting
/// for a delegator (TF-037), and access reviews (TF-038). Needs Iron.Kernel.Audit's writer
/// for denial and change events, so hosts list the Audit module before this one.
/// </summary>
public sealed class AuthorizationModule : IModule
{
    public string Name => "authz";

    public IReadOnlyList<Permission> Permissions => AuthorizationPermissions.All;

    public IReadOnlyList<Feature> Features => AuthorizationFeatures.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var cacheOptions = new PermissionCacheOptions();
        configuration.GetSection(PermissionCacheOptions.SectionName).Bind(cacheOptions);
        services.AddSingleton(cacheOptions);

        var rootUnit = new RootUnitSeedOptions();
        configuration.GetSection(RootUnitSeedOptions.SectionName).Bind(rootUnit);
        services.AddSingleton(rootUnit);

        services.AddMemoryCache();
        services.AddIronDbContext<AuthorizationDbContext>();

        // The kinds are an extension point (ADR-0029): a business module replaces this default.
        services.TryAddSingleton<IOrganisationKinds, DefaultOrganisationKinds>();

        services.AddScoped<ActingScope>();
        services.AddScoped<AccessGrants>();
        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddScoped<IRoleAssignments, RoleAssignments>();
        services.AddScoped<IRecordAccess, RecordAccessService>();
        services.AddScoped<IUnitReach, UnitReach>();
        services.AddScoped<IWorkingUnits, WorkingUnits>();
        services.AddScoped<OrganisationUnitService>();
        services.AddScoped<UnitMergeService>();
        services.AddScoped<AccessExplainer>();
        services.AddScoped<IOrganisationUnits>(provider => provider.GetRequiredService<OrganisationUnitService>());
        services.AddScoped<IOrganisationUnitCheck, OrganisationUnitCheck>();
        services.AddScoped<ISeeder, AuthorizationSeeder>();
        services.AddScoped<RoleService>();
        services.AddScoped<AssignmentService>();
        services.AddScoped<DelegationService>();
        services.AddScoped<IDelegations, DelegationQueries>();
        services.AddScoped<ReviewService>();
        services.AddScoped<UnitReviewers>();
        services.AddScoped<ReviewEscalationPass>();

        // Who hears of late reviewers is an extension point (ADR-0037): a business module that knows managers replaces it.
        services.TryAddScoped<IReviewEscalation, CampaignOwnerEscalation>();
        services.AddScoped<IValidator<StartReviewRequest>, StartReviewRequestValidator>();
        services.AddScoped<IValidator<DecideItemRequest>, DecideItemRequestValidator>();
        services.AddScoped<IValidator<ConfirmItemsRequest>, ConfirmItemsRequestValidator>();
        services.AddScoped<IValidator<CreateDelegationRequest>, CreateDelegationRequestValidator>();
        services.AddScoped<IValidator<GrantRoleRequest>, GrantRoleRequestValidator>();
        services.AddScoped<IValidator<CreateRoleRequest>, CreateRoleRequestValidator>();
        services.AddScoped<IValidator<UpdateRoleRequest>, UpdateRoleRequestValidator>();
        services.AddScoped<IValidator<ImportRolesRequest>, ImportRolesRequestValidator>();
        services.AddScoped<IValidator<CreateUnitRequest>, CreateUnitRequestValidator>();
        services.AddScoped<IValidator<RenameUnitRequest>, RenameUnitRequestValidator>();
        services.AddScoped<IValidator<MoveUnitRequest>, MoveUnitRequestValidator>();
        services.AddScoped<IValidator<MergeUnitRequest>, MergeUnitRequestValidator>();
        services.AddScoped<IValidator<ChangeSharingRequest>, ChangeSharingRequestValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RolesEndpoints.Map(endpoints);
        RoleAssignmentsEndpoints.Map(endpoints);
        OrganisationUnitsEndpoints.Map(endpoints);
        DelegationsEndpoints.Map(endpoints);
        AccessReviewsEndpoints.Map(endpoints);
        RecordSharingEndpoints.Map(endpoints);
        AccessExplainEndpoints.Map(endpoints);
    }
}
