using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Api;

/// <summary>A provider as the sign-in page offers it: a button with its name.</summary>
public sealed record SsoProviderButton(Guid Id, string Name)
{
    public static SsoProviderButton From(SsoProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new SsoProviderButton(provider.Id, provider.Name);
    }
}

public sealed record SsoDiscoverRequest(string Email);

/// <summary>The provider of the email's domain, or none: the same answer for every email of a domain, so it says nothing about accounts.</summary>
public sealed record SsoDiscoverResponse(SsoProviderButton? Provider);

public sealed record SsoStartRequest(Guid ProviderId, string? ReturnTo, string? LoginHint);

public sealed record SsoStartResponse(string AuthorizationUrl, DateTimeOffset ExpiresAt);

/// <summary>What the provider sent the browser back with, as the shell's callback page read it from the address.</summary>
public sealed record SsoCallbackRequest(string? Code, string? State, string? Error, string? ErrorDescription);

/// <summary>Where signing out at the provider happens: its end-session endpoint, with this tenant's client and the way back here.</summary>
public sealed record SsoSignOutResponse(string Url);

public sealed record SsoRoleMappingRequest(string Group, string RoleCode, Guid? UnitId, bool IncludeChildren);

/// <summary>A provider's settings; on a change, an empty client secret keeps the one in the store.</summary>
public sealed record SsoProviderRequest(
    string Name,
    string Preset,
    string DiscoveryUrl,
    string ClientId,
    string? ClientSecret,
    IReadOnlyList<string> AllowedDomains,
    string? EmailClaim,
    string? NameClaim,
    string? GroupsClaim,
    bool JustInTime,
    string? DefaultRoleCode,
    Guid? DefaultHomeUnitId,
    bool SsoOnly,
    bool TrustProviderMfa,
    IReadOnlyList<SsoRoleMappingRequest>? RoleMappings)
{
    public SsoProviderValues Values => new(
        Name ?? string.Empty,
        Preset ?? string.Empty,
        DiscoveryUrl ?? string.Empty,
        ClientId ?? string.Empty,
        AllowedDomains ?? [],
        string.IsNullOrWhiteSpace(EmailClaim) ? "email" : EmailClaim,
        string.IsNullOrWhiteSpace(NameClaim) ? "name" : NameClaim,
        string.IsNullOrWhiteSpace(GroupsClaim) ? "groups" : GroupsClaim,
        JustInTime,
        DefaultRoleCode,
        DefaultHomeUnitId,
        SsoOnly,
        TrustProviderMfa,
        [.. (RoleMappings ?? []).Select(m => new SsoRoleMapping(m.Group ?? string.Empty, m.RoleCode ?? string.Empty, m.UnitId, m.IncludeChildren))]);
}

/// <summary>A provider as the console shows it: everything but the client secret, which the store holds and nobody reads back.</summary>
public sealed record SsoProviderResponse(
    Guid Id,
    string Name,
    string Preset,
    string DiscoveryUrl,
    string ClientId,
    IReadOnlyList<string> AllowedDomains,
    string EmailClaim,
    string NameClaim,
    string GroupsClaim,
    bool JustInTime,
    string? DefaultRoleCode,
    Guid? DefaultHomeUnitId,
    bool SsoOnly,
    bool TrustProviderMfa,
    IReadOnlyList<SsoRoleMapping> RoleMappings,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    public static SsoProviderResponse From(SsoProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new SsoProviderResponse(
            provider.Id,
            provider.Name,
            provider.Preset,
            provider.DiscoveryUrl,
            provider.ClientId,
            provider.AllowedDomains,
            provider.EmailClaim,
            provider.NameClaim,
            provider.GroupsClaim,
            provider.JustInTime,
            provider.DefaultRoleCode,
            provider.DefaultHomeUnitId,
            provider.SsoOnly,
            provider.TrustProviderMfa,
            provider.RoleMappings,
            provider.CreatedAt,
            provider.UpdatedAt,
            provider.Version);
    }
}

public sealed record SsoTestRequest(string DiscoveryUrl);

public sealed record SsoTestResponse(bool Passed, IReadOnlyList<SsoTestItem> Items);

public sealed class SsoDiscoverRequestValidator : AbstractValidator<SsoDiscoverRequest>
{
    public SsoDiscoverRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().MaximumLength(Principal.LoginIdMaxLength);
    }
}

public sealed class SsoStartRequestValidator : AbstractValidator<SsoStartRequest>
{
    public SsoStartRequestValidator()
    {
        RuleFor(r => r.ProviderId).NotEmpty();
        RuleFor(r => r.ReturnTo).MaximumLength(500);
        RuleFor(r => r.LoginHint).MaximumLength(Principal.LoginIdMaxLength);
    }
}

public sealed class SsoCallbackRequestValidator : AbstractValidator<SsoCallbackRequest>
{
    public SsoCallbackRequestValidator()
    {
        RuleFor(r => r.Code).MaximumLength(4096);
        RuleFor(r => r.State).MaximumLength(200);
        RuleFor(r => r.Error).MaximumLength(200);
        RuleFor(r => r.ErrorDescription).MaximumLength(1000);
    }
}

public sealed class SsoProviderRequestValidator : AbstractValidator<SsoProviderRequest>
{
    public SsoProviderRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(SsoProvider.NameMaxLength);
        RuleFor(r => r.Preset).NotEmpty().MaximumLength(SsoProvider.PresetMaxLength);
        RuleFor(r => r.DiscoveryUrl).NotEmpty().MaximumLength(SsoProvider.DiscoveryUrlMaxLength);
        RuleFor(r => r.ClientId).NotEmpty().MaximumLength(SsoProvider.ClientIdMaxLength);
        RuleFor(r => r.ClientSecret).MaximumLength(SsoProviderService.ClientSecretMaxLength);
        RuleFor(r => r.AllowedDomains).NotNull();
        RuleFor(r => r.EmailClaim).MaximumLength(SsoProvider.ClaimMaxLength);
        RuleFor(r => r.NameClaim).MaximumLength(SsoProvider.ClaimMaxLength);
        RuleFor(r => r.GroupsClaim).MaximumLength(SsoProvider.ClaimMaxLength);
        RuleFor(r => r.DefaultRoleCode).MaximumLength(100);
    }
}

public sealed class SsoTestRequestValidator : AbstractValidator<SsoTestRequest>
{
    public SsoTestRequestValidator()
    {
        RuleFor(r => r.DiscoveryUrl).NotEmpty().MaximumLength(SsoProvider.DiscoveryUrlMaxLength);
    }
}

/// <summary>
/// Single sign-on (TF-014, ADR-0048). The sign-in's doors are anonymous: discovering a provider by an email's domain,
/// starting a sign-in, completing it with what the provider sent back, and the address to sign out at the provider. The
/// console's are behind the critical <c>identity.sso.manage</c>, and every change behind a recent authentication.
/// </summary>
internal static class SsoEndpoints
{
    public static readonly ListFields<SsoProvider> Fields = new ListFields<SsoProvider>()
        .Add("name", p => p.Name, FieldUse.All)
        .SortByDefault("name");

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth/sso").WithTags("auth");
        auth.MapPost("/discover", DiscoverAsync).Validate<SsoDiscoverRequest>().AllowAnonymous().Produces<SsoDiscoverResponse>(StatusCodes.Status200OK).WithName("SsoDiscover").WithSummary("The company login of an email's domain, if this tenant has one: the same answer for every email of a domain.");
        auth.MapPost("/start", StartAsync).Validate<SsoStartRequest>().AllowAnonymous().Produces<SsoStartResponse>(StatusCodes.Status200OK).WithName("SsoStart").WithSummary("Start a sign-in through a provider: where to send the browser; the sign-in cookie holds the rest.");
        auth.MapPost("/callback", CallbackAsync).Validate<SsoCallbackRequest>().AllowAnonymous().Produces<LoginResponse>(StatusCodes.Status200OK).WithName("SsoCallback").WithSummary("Complete a sign-in with what the provider sent back: the session, or the step that comes next.");
        auth.MapGet("/{id:guid}/sign-out", SignOutAsync).AllowAnonymous().Produces<SsoSignOutResponse>(StatusCodes.Status200OK).WithName("SsoSignOut").WithSummary("Where to sign out at a provider, when it publishes an end-session endpoint.");

        var sso = endpoints.MapApiGroup("sso").WithTags("sso");
        sso.MapGet("/providers", ListAsync).RequirePermission(IdentityPermissions.SsoManage).Produces<ListResponse<SsoProviderResponse>>(StatusCodes.Status200OK).WithName("ListSsoProviders").WithSummary("The tenant's single sign-on providers.");
        sso.MapGet("/providers/{id:guid}", GetAsync).RequirePermission(IdentityPermissions.SsoManage).Produces<SsoProviderResponse>(StatusCodes.Status200OK).WithName("GetSsoProvider").WithSummary("One single sign-on provider, without its client secret.");
        sso.MapPost("/providers", CreateAsync).RequirePermission(IdentityPermissions.SsoManage).RequireRecentAuthentication().Validate<SsoProviderRequest>().Produces<SsoProviderResponse>(StatusCodes.Status201Created).WithName("CreateSsoProvider").WithSummary("Connect a provider; its client secret goes to the store. A default role and every mapping must be roles you may grant.");
        sso.MapPut("/providers/{id:guid}", UpdateAsync).RequirePermission(IdentityPermissions.SsoManage).RequireRecentAuthentication().Validate<SsoProviderRequest>().Produces<SsoProviderResponse>(StatusCodes.Status200OK).WithName("UpdateSsoProvider").WithSummary("Change a provider; an empty client secret keeps the one in the store.");
        sso.MapDelete("/providers/{id:guid}", DeleteAsync).RequirePermission(IdentityPermissions.SsoManage).RequireRecentAuthentication().Produces(StatusCodes.Status204NoContent).WithName("DeleteSsoProvider").WithSummary("Remove a provider, its links and the roles its mapping gave; the people stay.");
        sso.MapPost("/test", TestAsync).RequirePermission(IdentityPermissions.SsoManage).Validate<SsoTestRequest>().Produces<SsoTestResponse>(StatusCodes.Status200OK).WithName("TestSsoProvider").WithSummary("Test a provider's discovery document and keys, and say in words what works and what does not.");
    }

    /// <summary>
    /// The address the provider sends the browser back to: the shell's callback page on the tenant's hostname the request
    /// came to, over HTTPS; a hostname the tenant does not have is never echoed (ADR-0048).
    /// </summary>
    internal static string RedirectUri(HttpContext http)
    {
        var tenant = http.RequestServices.GetService(typeof(TenantContext)) as TenantContext;
        var host = http.Request.Host.Host;
        var hostname = tenant is null || tenant.Hostnames.Contains(host, StringComparer.OrdinalIgnoreCase) ? host : tenant.Hostnames[0];
        return $"https://{hostname}{SsoSignInService.CallbackPath}";
    }

    private static async Task<IResult> DiscoverAsync(SsoDiscoverRequest request, SsoPeople people, CancellationToken cancellationToken)
    {
        var provider = await people.ForEmailAsync(request.Email, cancellationToken);
        return TypedResults.Ok(new SsoDiscoverResponse(provider is null ? null : SsoProviderButton.From(provider)));
    }

    private static async Task<IResult> StartAsync(SsoStartRequest request, HttpContext http, SsoSignInService sso, CancellationToken cancellationToken)
    {
        var result = await sso.StartAsync(request.ProviderId, RedirectUri(http), request.ReturnTo, request.LoginHint, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result.Error, http);
        }

        AuthenticationChallenges.SetCookie(http, result.Value.ChallengeToken, ChallengePurposes.SsoSignIn);
        return TypedResults.Ok(new SsoStartResponse(result.Value.AuthorizationUrl.AbsoluteUri, result.Value.ExpiresAt));
    }

    private static async Task<IResult> CallbackAsync(SsoCallbackRequest request, HttpContext http, SsoSignInService sso, IdentityOptions options, CancellationToken cancellationToken) =>
        FactorEndpoints.SignIn(await sso.CompleteAsync(http, request.Code, request.State, request.Error, cancellationToken), http, options);

    private static async Task<IResult> SignOutAsync(Guid id, HttpContext http, IdentityDbContext context, OidcDiscovery discovery, CancellationToken cancellationToken)
    {
        var provider = await context.SsoProviders.FindAsync([id], cancellationToken);
        var metadata = provider is null ? null : await discovery.MetadataAsync(provider.DiscoveryUrl, cancellationToken);
        if (provider is null || metadata is null || metadata.IsFailure || metadata.Value.EndSessionEndpoint is not { } endpoint)
        {
            return ApiResults.Problem(Error.NotFound(IdentityErrors.SsoNoEndSession, "There is nothing to sign out of at this provider."), http);
        }

        var back = RedirectUri(http).Replace(SsoSignInService.CallbackPath, "/login", StringComparison.Ordinal);
        var separator = endpoint.AbsoluteUri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return TypedResults.Ok(new SsoSignOutResponse($"{endpoint.AbsoluteUri}{separator}client_id={Uri.EscapeDataString(provider.ClientId)}&post_logout_redirect_uri={Uri.EscapeDataString(back)}"));
    }

    private static async Task<IResult> ListAsync(HttpContext http, IdentityDbContext context, CancellationToken cancellationToken)
    {
        var parsed = ListQueryParser.Parse(http.Request.Query, Fields);
        if (parsed.IsFailure)
        {
            return ApiResults.Problem(parsed.Error, http);
        }

        var query = parsed.Value;
        var page = await context.SsoProviders.AsNoTracking().ApplyListQuery(query, Fields).ToPageAsync(query.Page, cancellationToken);
        return ApiResults.List(page.Map(SsoProviderResponse.From), http, query);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, SsoProviderService providers, CancellationToken cancellationToken) =>
        ApiResults.From(await providers.FindAsync(id, cancellationToken), http, provider => TypedResults.Ok(SsoProviderResponse.From(provider)));

    private static async Task<IResult> CreateAsync(SsoProviderRequest request, HttpContext http, SsoProviderService providers, CancellationToken cancellationToken)
    {
        var result = await providers.CreateAsync(request.Values, request.ClientSecret ?? string.Empty, cancellationToken);
        return ApiResults.From(result, http, provider => TypedResults.Created($"{ApiRouting.Prefix}/sso/providers/{provider.Id}", SsoProviderResponse.From(provider)));
    }

    private static async Task<IResult> UpdateAsync(Guid id, SsoProviderRequest request, HttpContext http, SsoProviderService providers, CancellationToken cancellationToken) =>
        ApiResults.From(await providers.UpdateAsync(id, request.Values, request.ClientSecret, cancellationToken), http, provider => TypedResults.Ok(SsoProviderResponse.From(provider)));

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, SsoProviderService providers, CancellationToken cancellationToken) =>
        ApiResults.From(await providers.DeleteAsync(id, cancellationToken), http, () => TypedResults.NoContent());

    private static async Task<IResult> TestAsync(SsoTestRequest request, SsoProviderService providers, CancellationToken cancellationToken)
    {
        var report = await providers.TestAsync(request.DiscoveryUrl.Trim(), cancellationToken);
        return TypedResults.Ok(new SsoTestResponse(report.Passed, report.Items));
    }
}
