using FluentValidation;
using Iron.Api.Modules;
using Iron.Core.Permissions;
using Iron.Core.Principals;
using Iron.Data;
using Iron.Data.Seeding;
using Iron.Identity.Api;
using Iron.Identity.Contracts;
using Iron.Identity.Data;
using Iron.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Identity;

/// <summary>
/// Principals, passwords, sessions and invitations (TF-010, TF-011, TF-018, TF-020, TF-021, TF-029; part 1 in S1).
/// Replaces the default system principal with the request principal, so hosts list it after Audit and Authorization
/// and call <c>UseIronIdentity</c> after UseIronTenant.
/// </summary>
public sealed class IdentityModule : IModule
{
    public string Name => "identity";

    public IReadOnlyList<Permission> Permissions => IdentityPermissions.All;

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

        services.AddHttpContextAccessor();
        services.AddIronDbContext<IdentityDbContext>();
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddScoped<SessionService>();
        services.AddScoped<LoginService>();
        services.AddScoped<InvitationService>();
        services.AddScoped<RequestPrincipal>();
        services.AddScoped<ICurrentPrincipal>(provider => provider.GetRequiredService<RequestPrincipal>());
        services.AddScoped<ISeeder, IdentitySeeder>();
        services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
        services.AddScoped<IValidator<AcceptInvitationRequest>, AcceptInvitationRequestValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        AuthEndpoints.Map(endpoints);
    }

}

public static class IdentityApplicationBuilderExtensions
{
    /// <summary>The session middleware: cookie to principal. Call after UseIronTenant and before the endpoints.</summary>
    public static IApplicationBuilder UseIronIdentity(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SessionMiddleware>();
    }
}
