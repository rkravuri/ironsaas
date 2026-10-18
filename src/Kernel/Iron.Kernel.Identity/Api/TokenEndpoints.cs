using System.Net.Http.Headers;
using System.Text;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Telemetry.Correlation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Api;

/// <summary>The OAuth 2.1 token response (RFC 6749 section 5.1).</summary>
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string Scope);

/// <summary>The OAuth 2.1 error response (RFC 6749 section 5.2), with the correlation id for support.</summary>
public sealed record TokenErrorResponse(string Error, string ErrorDescription, string CorrelationId);

/// <summary>
/// <c>POST /connect/token</c>: the client credentials grant for service accounts (TF-023), and
/// <c>GET /.well-known/jwks.json</c>: the public keys. Both are anonymous by nature and on the fixed allow-list.
/// The token endpoint speaks OAuth, not problem details, because OAuth clients expect the OAuth error shape.
/// </summary>
internal static class TokenEndpoints
{
    public const string TokenPath = "/connect/token";
    public const string JwksPath = "/.well-known/jwks.json";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(TokenPath, TokenAsync).AllowAnonymous().WithTags("oauth").WithName("Token").WithSummary("Client credentials grant: an access token for a service account.");
        endpoints.MapGet(JwksPath, (SigningKeys keys) => TypedResults.Ok(keys.JsonWebKeySet())).AllowAnonymous().WithTags("oauth").WithName("JsonWebKeySet").WithSummary("The public keys that sign access tokens.");
    }

    private static async Task<IResult> TokenAsync(HttpContext http, IdentityDbContext context, IPasswordHasher hasher, TokenService tokens, TokenOptions options, IAuditWriter audit, CancellationToken cancellationToken)
    {
        if (!http.Request.HasFormContentType)
        {
            return Error(http, StatusCodes.Status400BadRequest, "invalid_request", "Send the request as application/x-www-form-urlencoded.");
        }

        var form = await http.Request.ReadFormAsync(cancellationToken);
        var grantType = form["grant_type"].ToString();
        if (!string.Equals(grantType, "client_credentials", StringComparison.Ordinal))
        {
            return Error(http, StatusCodes.Status400BadRequest, "unsupported_grant_type", "Only the client_credentials grant is supported.");
        }

        var (clientId, secret) = ReadClientCredentials(http, form);
        var normalised = Principal.NormaliseLoginId(clientId);
        var client = normalised.Length == 0 ? null : await context.Principals.SingleOrDefaultAsync(p => p.LoginId == normalised, cancellationToken);
        var reason = Check(client, secret, hasher);
        if (reason is not null || client is null)
        {
            await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.TokenRefused, AuditOutcome.Denied, client?.Id ?? Guid.Empty, normalised, Reason: reason ?? "unknown_client"), cancellationToken);
            http.Response.Headers.WWWAuthenticate = "Basic realm=\"token\"";
            return Error(http, StatusCodes.Status401Unauthorized, "invalid_client", "The client credentials are not correct.");
        }

        var issued = tokens.Issue(client, form["scope"].ToString());
        await audit.WriteAsync(new AuditEvent(IdentityAuditEvents.TokenIssued, AuditOutcome.Success, client.Id, client.LoginId, RecordType: "token", RecordId: issued.TokenId), cancellationToken);
        return TypedResults.Ok(new TokenResponse(issued.AccessToken, "Bearer", (int)options.AccessTokenLifetime.TotalSeconds, issued.Scope));
    }

    /// <summary>Null when the client may have a token; otherwise the reason for the audit event. Every path verifies once, so timing reveals nothing.</summary>
    private static string? Check(Principal? client, string secret, IPasswordHasher hasher)
    {
        if (client is null)
        {
            hasher.VerifyOrDecoy(secret, null);
            return "unknown_client";
        }

        if (client.Type != PrincipalType.ServiceAccount)
        {
            hasher.VerifyOrDecoy(secret, client.PasswordHash);
            return "not_a_service_account";
        }

        if (client.Status != PrincipalStatus.Active)
        {
            hasher.VerifyOrDecoy(secret, client.PasswordHash);
            return "inactive";
        }

        return secret.Length > 0 && hasher.VerifyOrDecoy(secret, client.PasswordHash) ? null : "wrong_secret";
    }

    /// <summary>HTTP Basic first (the OAuth default), then the form fields.</summary>
    private static (string ClientId, string Secret) ReadClientCredentials(HttpContext http, IFormCollection form)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (AuthenticationHeaderValue.TryParse(header, out var value) && string.Equals(value.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) && value.Parameter is not null)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value.Parameter));
                var colon = decoded.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    return (Uri.UnescapeDataString(decoded[..colon]), Uri.UnescapeDataString(decoded[(colon + 1)..]));
                }
            }
            catch (FormatException)
            {
                // not base64: fall through to the form fields
            }
        }

        return (form["client_id"].ToString(), form["client_secret"].ToString());
    }

    private static IResult Error(HttpContext http, int status, string error, string description) =>
        TypedResults.Json(new TokenErrorResponse(error, description, RequestCorrelation.Get(http).Value), statusCode: status);
}
