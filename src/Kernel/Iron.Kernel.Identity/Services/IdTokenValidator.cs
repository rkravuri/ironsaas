using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Iron.Kernel.Identity.Services;

/// <summary>Who the provider says signed in, read from a checked ID token with the provider's claim names (ADR-0048).</summary>
public sealed record IdTokenIdentity(string Subject, string? Email, bool EmailVerified, string? Name, IReadOnlyList<string> Groups, IReadOnlyList<string> Methods)
{
    /// <summary>
    /// The provider says the sign-in used more than one factor: its <c>amr</c> names <c>mfa</c>, as RFC 8176 defines it and
    /// as Microsoft Entra ID and Okta write it. Nothing else is taken for it.
    /// </summary>
    public bool MultiFactor => Methods.Contains("mfa", StringComparer.Ordinal);
}

/// <summary>What an ID token must say: who issued it, for which client, with which nonce; and the provider's claim names.</summary>
public sealed record IdTokenExpectations(string Issuer, string ClientId, string Nonce, string EmailClaim, string NameClaim, string GroupsClaim);

/// <summary>A checked ID token's identity, or why it was refused, for the audit trail; the person is told nothing more than that it failed.</summary>
public sealed record IdTokenCheck(IdTokenIdentity? Identity, string? Failure)
{
    public static IdTokenCheck Refused(string failure) => new(null, failure);
}

/// <summary>
/// Checks an ID token in full (OpenID Connect Core 3.1.3.7, ADR-0048): signed with an asymmetric algorithm by a key the
/// provider publishes, the provider's issuer, this tenant's client among its audiences and as the authorized party when
/// there are several, not expired, not issued in the future nor long ago, and the nonce of this sign-in. Times are read on
/// the unit's clock with two minutes' tolerance either way. A token that names a key the provider's set does not hold has
/// the keys read again once, for a provider that rotated its keys since the unit last read them.
/// </summary>
public sealed class IdTokenValidator(TimeProvider time)
{
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(2);

    /// <summary>An ID token comes straight from the code's exchange, so one issued longer ago than this is refused.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(10);

    private static readonly string[] Algorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    /// <summary>
    /// Checks <paramref name="idToken"/>; <paramref name="keys"/> answers the provider's keys, read again when asked
    /// (<c>true</c>), as <see cref="OidcDiscovery.KeysAsync"/> does.
    /// </summary>
    public async Task<IdTokenCheck> CheckAsync(string idToken, IdTokenExpectations expected, Func<bool, CancellationToken, Task<Result<JsonWebKeySet>>> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(keys);
        if (string.IsNullOrWhiteSpace(idToken) || !_handler.CanReadToken(idToken))
        {
            return IdTokenCheck.Refused("not_a_token");
        }

        JsonWebToken token;
        JsonElement payload;
        try
        {
            token = _handler.ReadJsonWebToken(idToken);
            using var document = JsonDocument.Parse(Base64UrlEncoder.Decode(token.EncodedPayload));
            payload = document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException)
        {
            return IdTokenCheck.Refused("not_a_token");
        }

        var set = await keys(false, cancellationToken).ConfigureAwait(false);
        if (set.IsFailure)
        {
            return IdTokenCheck.Refused("keys_unavailable");
        }

        if (!string.IsNullOrEmpty(token.Kid) && set.Value.Keys.All(k => k.KeyId != token.Kid))
        {
            set = await keys(true, cancellationToken).ConfigureAwait(false);
            if (set.IsFailure || set.Value.Keys.All(k => k.KeyId != token.Kid))
            {
                return IdTokenCheck.Refused("unknown_signing_key");
            }
        }

        var result = await _handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = expected.Issuer,
            ValidateIssuer = true,
            ValidAudience = expected.ClientId,
            ValidateAudience = true,
            IssuerSigningKeys = set.Value.GetSigningKeys(),
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidAlgorithms = Algorithms,

            // Read below on the unit's clock, so a test's clock and the server's are the same thing.
            ValidateLifetime = false,
            RequireExpirationTime = false,
        }).ConfigureAwait(false);
        if (!result.IsValid)
        {
            return IdTokenCheck.Refused(result.Exception switch
            {
                SecurityTokenInvalidIssuerException => "wrong_issuer",
                SecurityTokenInvalidAudienceException => "wrong_audience",
                SecurityTokenInvalidAlgorithmException => "wrong_algorithm",
                SecurityTokenSignatureKeyNotFoundException => "unknown_signing_key",
                SecurityTokenInvalidSignatureException => "bad_signature",
                _ => "invalid_token",
            });
        }

        var now = time.GetUtcNow();
        if (!token.TryGetPayloadValue<long>("exp", out var exp) || DateTimeOffset.FromUnixTimeSeconds(exp) + ClockTolerance <= now)
        {
            return IdTokenCheck.Refused("expired");
        }

        if (token.TryGetPayloadValue<long>("nbf", out var nbf) && DateTimeOffset.FromUnixTimeSeconds(nbf) - ClockTolerance > now)
        {
            return IdTokenCheck.Refused("not_yet_valid");
        }

        if (!token.TryGetPayloadValue<long>("iat", out var iat))
        {
            return IdTokenCheck.Refused("no_issue_time");
        }

        var issued = DateTimeOffset.FromUnixTimeSeconds(iat);
        if (issued - ClockTolerance > now || now - issued > MaximumAge + ClockTolerance)
        {
            return IdTokenCheck.Refused("issue_time");
        }

        if (!token.TryGetPayloadValue<string>("nonce", out var nonce) || !CryptographicEquals(nonce, expected.Nonce))
        {
            return IdTokenCheck.Refused("wrong_nonce");
        }

        var audiences = token.Audiences.ToList();
        if (audiences.Count > 1 && (!token.TryGetPayloadValue<string>("azp", out var azp) || azp != expected.ClientId))
        {
            return IdTokenCheck.Refused("wrong_authorized_party");
        }

        if (string.IsNullOrWhiteSpace(token.Subject))
        {
            return IdTokenCheck.Refused("no_subject");
        }

        return new IdTokenCheck(
            new IdTokenIdentity(
                token.Subject,
                Text(payload, expected.EmailClaim)?.Trim(),
                Flag(payload, "email_verified"),
                Text(payload, expected.NameClaim)?.Trim(),
                Texts(payload, expected.GroupsClaim),
                Texts(payload, "amr")),
            null);
    }

    private static bool CryptographicEquals(string? a, string b) =>
        a is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    // The claims are read from the payload's own JSON, as the provider wrote them: a claim's name may be any string, such
    // as a URI, and its value a string, a boolean or a list.
    private static string? Text(JsonElement payload, string claim) =>
        payload.TryGetProperty(claim, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>True only for <c>true</c>, or the string "true" some providers send.</summary>
    private static bool Flag(JsonElement payload, string claim) =>
        payload.TryGetProperty(claim, out var value)
        && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    /// <summary>A claim's strings: an array of them, or one string.</summary>
    private static IReadOnlyList<string> Texts(JsonElement payload, string claim)
    {
        if (!payload.TryGetProperty(claim, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)],
            JsonValueKind.String => [value.GetString()!],
            _ => [],
        };
    }
}
