using FluentValidation;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.Data.Seeding;
using Iron.Kernel.History;
using Iron.Kernel.Identity.Api;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Security.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iron.Kernel.Identity;

/// <summary>
/// Principals, passwords, sessions, invitations and access tokens (TF-010, TF-011, TF-018, TF-020, TF-021, TF-023, TF-029);
/// the password policy, lockout, recovery links and security events (TF-012, TF-017, TF-019, ADR-0046); second factors,
/// the MFA policy and step-up re-authentication (TF-013, TF-248, ADR-0047); single sign-on with OpenID Connect (TF-014,
/// ADR-0048).
/// Replaces the default system principal with the request principal, so hosts list it after Audit and Authorization
/// and call <c>UseIronIdentity</c> after UseIronTenant. The session limits and an invitation's validity are settings
/// (ADR-0036), so a host that lists Identity lists the Settings module too.
/// </summary>
public sealed class IdentityModule : IModule
{
    public string Name => "identity";

    public IReadOnlyList<Permission> Permissions => IdentityPermissions.All;

    public IReadOnlyList<AuditEventType> AuditEventTypes => IdentityAuditEvents.Types;

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

        // The operator's support and emergency access (TF-025, TF-026, ADR-0049).
        var impersonation = new OperatorImpersonationOptions();
        configuration.GetSection(OperatorImpersonationOptions.SectionName).Bind(impersonation);
        services.AddSingleton(impersonation);
        var breakGlass = new BreakGlassOptions();
        configuration.GetSection(BreakGlassOptions.SectionName).Bind(breakGlass);
        services.AddSingleton(breakGlass);
        services.AddScoped<ImpersonationService>();
        services.AddScoped<BreakGlassAccounts>();
        services.AddScoped<IUnitCommand, OperatorImpersonationCommand>();
        services.AddScoped<IUnitCommand, BreakGlassOpenCommand>();
        services.AddScoped<IUnitCommand, BreakGlassCloseCommand>();
        services.AddScoped<IValidator<StartImpersonationRequest>, StartImpersonationRequestValidator>();
        services.AddScoped<IValidator<SignInLinkRequest>, SignInLinkRequestValidator>();

        // Terms acceptance (TF-028, ADR-0049): the platform's documents from the store, the tenant's from its database.
        services.AddScoped<TermsService>();
        services.AddSingleton<TenantDocumentVersions>();
        services.AddScoped<IValidator<AcceptTermsRequest>, AcceptTermsRequestValidator>();
        services.AddScoped<IValidator<PublishDocumentRequest>, PublishDocumentRequestValidator>();
        // The handover place is the secret store's to give (ADR-0046); without it, nothing is handed over.
        services.TryAddSingleton<Iron.Kernel.Handover.IOperatorHandover, Iron.Kernel.Handover.NoOperatorHandover>();

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
        services.AddScoped<SessionConsole>();
        services.AddSingleton<RevocationTimes>();
        services.TryAddSingleton<IIpLocations>(provider => new MmdbIpLocations(options.Locations.DatabasePath, provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MmdbIpLocations>>()));
        services.AddScoped<IValidator<EndSessionRequest>, EndSessionRequestValidator>();
        services.AddScoped<IValidator<RevokeRequest>, RevokeRequestValidator>();
        services.AddScoped<IValidator<RevokeTenantRequest>, RevokeTenantRequestValidator>();
        services.AddScoped<LoginService>();
        services.AddScoped<InvitationService>();
        services.AddScoped<IPrincipals, PrincipalDirectory>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitMergeParticipant, HomeUnitMerge>());
        services.AddScoped<UserService>();
        services.AddScoped<IServiceAccounts, ServiceAccounts>();
        services.AddRetainedRecords<SessionRetention>();
        services.AddScoped<IValidator<CreateUserRequest>, CreateUserRequestValidator>();
        services.AddScoped<RequestPrincipal>();
        services.AddScoped<ICurrentPrincipal>(provider => provider.GetRequiredService<RequestPrincipal>());
        services.AddScoped<ISeeder, IdentitySeeder>();
        services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
        services.AddScoped<IValidator<AcceptInvitationRequest>, AcceptInvitationRequestValidator>();
        services.AddScoped<IValidator<RecoverPasswordRequest>, RecoverPasswordRequestValidator>();
        services.AddScoped<IValidator<ChangePasswordRequest>, ChangePasswordRequestValidator>();

        // Second factors and step-up (TF-013, TF-248, ADR-0047).
        services.AddSingleton<FactorProtector>();
        services.AddScoped<Passkeys>();
        services.AddScoped<RecoveryCodes>();
        services.AddScoped<MfaPolicy>();
        services.AddScoped<AuthenticationChallenges>();
        services.AddScoped<FactorService>();
        services.AddScoped<ReauthenticationService>();

        // Proving the signer of an electronic signature (ADR-0051): Compliance declares it, Identity holds the credentials.
        services.AddScoped<ISignerAuthentication, SignerAuthentication>();
        services.Replace(ServiceDescriptor.Singleton<IRecentAuthentication, RequestRecentAuthentication>());
        services.AddScoped<IValidator<SecondFactorRequest>, SecondFactorRequestValidator>();
        services.AddScoped<IValidator<ConfirmAuthenticatorAppRequest>, ConfirmAuthenticatorAppRequestValidator>();
        services.AddScoped<IValidator<RegisterPasskeyRequest>, RegisterPasskeyRequestValidator>();
        services.AddScoped<IValidator<RenameFactorRequest>, RenameFactorRequestValidator>();
        services.AddScoped<IValidator<ReauthenticateRequest>, ReauthenticateRequestValidator>();
        services.AddScoped<IValidator<PasskeySignInRequest>, PasskeySignInRequestValidator>();
        services.AddScoped<IValidator<ResetFactorsRequest>, ResetFactorsRequestValidator>();

        // Single sign-on (TF-014, ADR-0048). A provider answers within ten seconds or the sign-in says it cannot be reached.
        services.AddHttpClient(OidcDiscovery.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<OidcDiscovery>();
        services.AddSingleton<IdTokenValidator>();
        services.AddScoped<SsoPeople>();
        services.AddScoped<SsoProviderService>();
        services.AddScoped<SsoSignInService>();
        var store = new SecretStoreOptions();
        configuration.GetSection(SecretStoreOptions.SectionName).Bind(store);
        if (store.IsConfigured)
        {
            services.AddScoped<ISsoClientSecrets>(provider => new StoreSsoClientSecrets(provider.GetRequiredService<ISecretStore>(), provider.GetRequiredService<SecretStoreOptions>()));
            services.TryAddSingleton<IPlatformDocuments>(provider => new StorePlatformDocuments(provider.GetRequiredService<ISecretStore>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StorePlatformDocuments>>()));
        }
        else
        {
            services.TryAddSingleton<ISsoClientSecrets, ProcessSsoClientSecrets>();
            services.TryAddSingleton<IPlatformDocuments, NoPlatformDocuments>();
        }

        services.AddScoped<IValidator<SsoDiscoverRequest>, SsoDiscoverRequestValidator>();
        services.AddScoped<IValidator<SsoStartRequest>, SsoStartRequestValidator>();
        services.AddScoped<IValidator<SsoCallbackRequest>, SsoCallbackRequestValidator>();
        services.AddScoped<IValidator<SsoProviderRequest>, SsoProviderRequestValidator>();
        services.AddScoped<IValidator<SsoTestRequest>, SsoTestRequestValidator>();
        services.AddScoped<IValidator<SsoReauthenticationRequest>, SsoReauthenticationRequestValidator>();
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
