using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Turns a bearer token into the request's principal (TF-020, TF-023). The token is checked on every request, and so is
/// the principal behind it: a disabled service account is refused at once, and so is a token issued before its
/// principal's or the tenant's "valid after" (TF-029 AC2, ADR-0049). A bad token sets
/// <c>auth.token_invalid</c>, a stale one <c>auth.token_expired</c>; the permission filter turns either into the 401.
/// Runs after the session middleware; a request carries a cookie or a token, and the token wins when both are present.
/// </summary>
public sealed class BearerTokenMiddleware(RequestDelegate next, ILogger<BearerTokenMiddleware> logger)
{
    private const string Scheme = "Bearer ";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            var token = header[Scheme.Length..].Trim();
            var validation = await context.RequestServices.GetRequiredService<TokenService>().ValidateAsync(token).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                logger.LogWarning("A bearer token was refused: {Reason}.", validation.Failure);
                AuthenticationFailure.Set(context, validation.Failure == TokenService.ExpiredReason ? PermissionErrors.TokenExpired : PermissionErrors.TokenInvalid);
            }
            else
            {
                var claims = validation.Claims!;
                var principal = await context.RequestServices.GetRequiredService<IdentityDbContext>().Principals.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.Id == claims.PrincipalId, context.RequestAborted)
                    .ConfigureAwait(false);
                if (principal is null || principal.Status != PrincipalStatus.Active)
                {
                    logger.LogWarning("A bearer token was refused: its principal {PrincipalId} is missing or not active.", claims.PrincipalId);
                    AuthenticationFailure.Set(context, PermissionErrors.TokenInvalid);
                }
                else if (RevocationTimes.IssuedBefore(claims.IssuedAt, principal.ValidAfter)
                    || RevocationTimes.IssuedBefore(claims.IssuedAt, await context.RequestServices.GetRequiredService<RevocationTimes>().TenantValidAfterAsync(context.RequestServices.GetRequiredService<IdentityDbContext>(), context.RequestAborted).ConfigureAwait(false)))
                {
                    // Issued before its principal's or the tenant's revocation (TF-029 AC2, ADR-0049): refused within its 15 minutes.
                    logger.LogWarning("A bearer token was refused: it was issued before a revocation of principal {PrincipalId} or of the tenant.", claims.PrincipalId);
                    AuthenticationFailure.Set(context, PermissionErrors.TokenRevoked);
                }
                else
                {
                    RequestPrincipal.Set(context, new SignedInPrincipal(principal.Id, principal.LoginId, principal.DisplayName, principal.Type, claims.TokenId, claims.ExpiresAt, claims.ExpiresAt, AuthenticationKind.Token, principal.HomeUnitId) { AuthenticatedAt = claims.IssuedAt });
                }
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
