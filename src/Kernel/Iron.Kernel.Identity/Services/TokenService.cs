using System.Text;
using System.Text.Json;
using Iron.Kernel.Identity.Data;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Iron.Kernel.Identity.Services;

public sealed record IssuedToken(string AccessToken, Guid TokenId, DateTimeOffset ExpiresAt, string Scope);

/// <summary>
/// What a valid bearer token says, after every check passed. <see cref="IssuedAt"/> is when its client last proved who it
/// is, with its credentials: a high-risk action needs it recent (TF-248).
/// </summary>
public sealed record TokenClaims(Guid PrincipalId, Guid TokenId, string ClientId, DateTimeOffset ExpiresAt, string Scope, DateTimeOffset? IssuedAt = null);

public sealed record TokenValidation(TokenClaims? Claims, string? Failure)
{
    public bool IsValid => Claims is not null;

    public static TokenValidation Valid(TokenClaims claims) => new(claims, null);

    public static TokenValidation Invalid(string reason) => new(null, reason);
}

/// <summary>
/// Issues and validates access tokens (TF-020): JWT (RFC 9068, <c>at+jwt</c>), ES256, 15 minutes, with issuer, audience
/// and the unit's tenant as claims. The key service signs with the latest version of the tenant's signing key, named in
/// the header; validation accepts every live version (TF-075 AC2, ADR-0054) and checks signature, type, algorithm,
/// issuer, audience and tenant through the library, and the lifetime with zero clock skew on Foundation's own clock.
/// </summary>
public sealed class TokenService(SigningKeys keys, TokenOptions options, TenantContext tenant, TimeProvider time)
{
    public const string TokenType = "at+jwt";
    public const string TenantClaim = "tenant";
    public const string ClientIdClaim = "client_id";
    public const string PrincipalTypeClaim = "principal_type";
    public const string ScopeClaim = "scope";
    public const string ExpiredReason = "expired";
    public const string NotYetValidReason = "not_yet_valid";
    public const string TenantMismatchReason = "tenant_mismatch";

    private readonly JsonWebTokenHandler _handler = new();

    public string Issuer => string.IsNullOrWhiteSpace(options.Issuer) ? $"https://{tenant.PrimaryHostname}" : options.Issuer;

    public async Task<IssuedToken> IssueAsync(Principal principal, string? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = time.GetUtcNow();
        var expiresAt = now + options.AccessTokenLifetime;
        var tokenId = Ids.New(time);
        var grantedScope = scope?.Trim() ?? string.Empty;
        var signing = await keys.CurrentAsync(cancellationToken).ConfigureAwait(false);

        var header = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JwtHeaderParameterNames.Alg] = SecurityAlgorithms.EcdsaSha256,
            [JwtHeaderParameterNames.Kid] = signing.KeyId,
            [JwtHeaderParameterNames.Typ] = TokenType,
        };
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Iss] = Issuer,
            [JwtRegisteredClaimNames.Aud] = options.Audience,
            [JwtRegisteredClaimNames.Iat] = now.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Nbf] = now.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Exp] = expiresAt.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Sub] = principal.Id.ToString("D"),
            [JwtRegisteredClaimNames.Jti] = tokenId.ToString("D"),
            [ClientIdClaim] = principal.LoginId,
            [TenantClaim] = tenant.Id.Value,
            [PrincipalTypeClaim] = principal.Type.ToString().ToLowerInvariant(),
            [ScopeClaim] = grantedScope,
        };

        // The signing input is the two encoded parts; the key service signs it, the signature is the third part (RFC 7515).
        var signingInput = $"{Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload))}";
        var signature = await keys.SignAsync(Encoding.ASCII.GetBytes(signingInput), signing.Version, cancellationToken).ConfigureAwait(false);
        return new IssuedToken($"{signingInput}.{Base64UrlEncoder.Encode(signature)}", tokenId, DateTimeOffset.FromUnixTimeSeconds(expiresAt.ToUnixTimeSeconds()), grantedScope);
    }

    public async Task<TokenValidation> ValidateAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return TokenValidation.Invalid("empty");
        }

        string? keyId = null;
        if (_handler.CanReadToken(token))
        {
            keyId = _handler.ReadJsonWebToken(token).Kid;
        }

        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKeys = await keys.ValidationKeysAsync(keyId, CancellationToken.None).ConfigureAwait(false),
            CryptoProviderFactory = keys.CryptoProviderFactory,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidTypes = [TokenType],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            // Checked below on Foundation's clock, with zero skew; CA5404 sees only the library's check switched off
            // (ADR-0007, ADR-0015). RequireExpirationTime stays on, so a token without "exp" never reaches the check.
#pragma warning disable CA5404
            ValidateLifetime = false,
#pragma warning restore CA5404
        }).ConfigureAwait(false);

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return TokenValidation.Invalid(result.Exception?.GetType().Name ?? "invalid");
        }

        var now = time.GetUtcNow();
        var validTo = Utc(jwt.ValidTo);
        if (now >= validTo)
        {
            return TokenValidation.Invalid(ExpiredReason);
        }

        if (jwt.ValidFrom != DateTime.MinValue && now < Utc(jwt.ValidFrom))
        {
            return TokenValidation.Invalid(NotYetValidReason);
        }

        if (!jwt.TryGetPayloadValue<string>(TenantClaim, out var tokenTenant) || !string.Equals(tokenTenant, tenant.Id.Value, StringComparison.Ordinal))
        {
            return TokenValidation.Invalid(TenantMismatchReason);
        }

        if (!Guid.TryParse(jwt.Subject, out var principalId) || !Guid.TryParse(jwt.Id, out var tokenId))
        {
            return TokenValidation.Invalid("subject");
        }

        jwt.TryGetPayloadValue<string>(ClientIdClaim, out var clientId);
        jwt.TryGetPayloadValue<string>(ScopeClaim, out var scope);
        var issuedAt = jwt.IssuedAt == DateTime.MinValue ? (DateTimeOffset?)null : Utc(jwt.IssuedAt);
        return TokenValidation.Valid(new TokenClaims(principalId, tokenId, clientId ?? string.Empty, validTo, scope ?? string.Empty, issuedAt));
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
}
