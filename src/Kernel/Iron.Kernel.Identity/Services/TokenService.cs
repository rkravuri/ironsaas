using Iron.Kernel.Identity.Data;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Iron.Kernel.Identity.Services;

public sealed record IssuedToken(string AccessToken, Guid TokenId, DateTimeOffset ExpiresAt, string Scope);

/// <summary>What a valid bearer token says, after every check passed.</summary>
public sealed record TokenClaims(Guid PrincipalId, Guid TokenId, string ClientId, DateTimeOffset ExpiresAt, string Scope);

public sealed record TokenValidation(TokenClaims? Claims, string? Failure)
{
    public bool IsValid => Claims is not null;

    public static TokenValidation Valid(TokenClaims claims) => new(claims, null);

    public static TokenValidation Invalid(string reason) => new(null, reason);
}

/// <summary>
/// Issues and validates access tokens (TF-020): JWT (RFC 9068, <c>at+jwt</c>), ES256, 15 minutes, with issuer, audience
/// and the unit's tenant as claims. Validation checks signature, type, algorithm, issuer, audience and tenant through
/// the library, and the lifetime with zero clock skew on Foundation's own clock.
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

    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public string Issuer => string.IsNullOrWhiteSpace(options.Issuer) ? $"https://{tenant.PrimaryHostname}" : options.Issuer;

    public IssuedToken Issue(Principal principal, string? scope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = time.GetUtcNow();
        var expiresAt = now + options.AccessTokenLifetime;
        var tokenId = Ids.New(time);
        var grantedScope = scope?.Trim() ?? string.Empty;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = options.Audience,
            TokenType = TokenType,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = keys.SigningCredentials,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Sub] = principal.Id.ToString("D"),
                [JwtRegisteredClaimNames.Jti] = tokenId.ToString("D"),
                [ClientIdClaim] = principal.LoginId,
                [TenantClaim] = tenant.Id.Value,
                [PrincipalTypeClaim] = principal.Type.ToString().ToLowerInvariant(),
                [ScopeClaim] = grantedScope,
            },
        };

        return new IssuedToken(_handler.CreateToken(descriptor), tokenId, expiresAt, grantedScope);
    }

    public async Task<TokenValidation> ValidateAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return TokenValidation.Invalid("empty");
        }

        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKeys = keys.ValidationKeys,
            CryptoProviderFactory = keys.CryptoProviderFactory,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidTypes = [TokenType],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = false, // checked below on Foundation's clock, with zero skew
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
        return TokenValidation.Valid(new TokenClaims(principalId, tokenId, clientId ?? string.Empty, validTo, scope ?? string.Empty));
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
}
