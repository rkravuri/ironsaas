using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Seeding;
using Iron.Kernel.Identity.Api;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Identity;

/// <summary>
/// Principals, passwords, sessions, invitations and access tokens (TF-010, TF-011, TF-018, TF-020, TF-021, TF-023, TF-029);
/// the password policy, lockout, recovery links and security events (TF-012, TF-017, TF-019, ADR-0046).
/// Replaces the default system principal with the request principal, so hosts list it after Audit and Authorization
/// and call <c>UseIronIdentity</c> after UseIronTenant. The session limits and an invitation's validity are settings
/// (ADR-0036), so a host that lists Identity lists the Settings module too.
/// </summary>
public sealed class IdentityModule : IModule
{
    public string Name => "identity";

    public IReadOnlyList<Permission> Permissions => IdentityPermissions.All;

    public IReadOnlyList<SettingDefinition> Settings => IdentitySettings.All;

    public IReadOnlyList<Duty> Duties => IdentityDuties.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new IdentityOptions();
        configuration.GetSection(IdentityOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        var seed = new AdministratorSeedOptions();
        configuration.GetSection(AdministratorSeedOptions.SectionName).Bind(seed);
        services.AddSingleton(seed);

        var recovery = new AdministratorRecoveryOptions();
        configuration.GetSection(AdministratorRecoveryOptions.SectionName).Bind(recovery);
        services.AddSingleton(recovery);
        services.AddScoped<IUnitCommand, AdministratorRecoveryCommand>();

        var serviceAccounts = configuration.GetSection(ServiceAccountSeedOptions.SectionName).Get<List<ServiceAccountSeedOptions>>() ?? [];
        services.AddSingleton(new ServiceAccountSeeds(serviceAccounts));

        var tokens = new TokenOptions();
        configuration.GetSection(TokenOptions.SectionName).Bind(tokens);
        services.AddSingleton(tokens);
        services.AddSingleton<SigningKeys>();
        services.AddScoped<TokenService>();

        services.AddHttpContextAccessor();
        services.AddIronDbContext<IdentityDbContext>();
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<CommonPasswords>();
        services.AddScoped<PasswordPolicy>();
        services.AddScoped<LoginThrottles>();
        services.AddScoped<SecurityEventLog>();
        services.AddScoped<PasswordService>();
        services.AddScoped<SessionService>();
        services.AddScoped<LoginService>();
        services.AddScoped<InvitationService>();
        services.AddScoped<IPrincipals, PrincipalDirectory>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitMergeParticipant, HomeUnitMerge>());
        services.AddScoped<UserService>();
        services.AddScoped<IValidator<CreateUserRequest>, CreateUserRequestValidator>();
        services.AddScoped<RequestPrincipal>();
        services.AddScoped<ICurrentPrincipal>(provider => provider.GetRequiredService<RequestPrincipal>());
        services.AddScoped<ISeeder, IdentitySeeder>();
        services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
        services.AddScoped<IValidator<AcceptInvitationRequest>, AcceptInvitationRequestValidator>();
        services.AddScoped<IValidator<RecoverPasswordRequest>, RecoverPasswordRequestValidator>();
        services.AddScoped<IValidator<ChangePasswordRequest>, ChangePasswordRequestValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        AuthEndpoints.Map(endpoints);
    }

}

public static class IdentityApplicationBuilderExtensions
{
    /// <summary>The session and bearer middleware: cookie or token to principal. Call after UseIronTenant and before the endpoints.</summary>
    public static IApplicationBuilder UseIronIdentity(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SessionMiddleware>().UseMiddleware<BearerTokenMiddleware>();
    }
}
