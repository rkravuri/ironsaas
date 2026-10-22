using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Iron.Kernel.Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Identity.Services;

/// <summary>A passkey the authenticator created and the library verified, ready to be saved as a factor.</summary>
public sealed record NewPasskey(byte[] CredentialId, byte[] PublicKey, long SignCount, byte[] UserHandle, Guid? AaGuid);

/// <summary>
/// WebAuthn for passkeys (TF-013, ADR-0047), through Fido2NetLib. The relying party is the tenant's hostname the request
/// came to, and the origins are the tenant's hostnames over HTTPS, so a passkey made for one tenant never signs in to
/// another. Every ceremony requires user verification, which makes a passkey a full second factor; a passkey is
/// discoverable, so a tenant that allows it can sign people in with the passkey alone. Attestation is not asked for: the
/// tenant trusts the person's choice of authenticator, as it trusts their choice of authenticator app. A ceremony's options
/// are kept with the challenge and the answer is checked against them.
/// </summary>
public sealed class Passkeys(IdentityDbContext context, TenantContext tenant, IHttpContextAccessor accessor, ILogger<Passkeys> logger)
{
    private const string RelyingPartyName = "IronSaaS";

    /// <summary>The user handle a person's passkeys carry: their id, which says nothing else about them.</summary>
    public static byte[] UserHandle(Guid principalId) => principalId.ToByteArray();

    public string RegistrationOptions(Principal principal, IReadOnlyList<Factor> existing)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(existing);
        var options = Library().RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = UserHandle(principal.Id), Name = principal.LoginId, DisplayName = principal.DisplayName },
            ExcludeCredentials = [.. existing.Where(f => f.IsPasskey).Select(f => new PublicKeyCredentialDescriptor(f.CredentialId!))],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        return options.ToJson();
    }

    /// <summary>The authenticator's answer to <paramref name="optionsJson"/>, verified; null when it does not verify.</summary>
    public async Task<NewPasskey?> RegisterAsync(string optionsJson, JsonElement credential, CancellationToken cancellationToken)
    {
        try
        {
            var response = credential.Deserialize<AuthenticatorAttestationRawResponse>()
                ?? throw new JsonException("The credential is empty.");
            var registered = await Library().MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = response,
                    OriginalOptions = CredentialCreateOptions.FromJson(optionsJson),
                    IsCredentialIdUniqueToUserCallback = async (candidate, token) =>
                        !await context.Factors.AnyAsync(f => f.CredentialId == candidate.CredentialId, token).ConfigureAwait(false),
                },
                cancellationToken).ConfigureAwait(false);
            return new NewPasskey(registered.Id, registered.PublicKey, registered.SignCount, registered.User.Id, registered.AaGuid == Guid.Empty ? null : registered.AaGuid);
        }
        catch (Exception exception) when (exception is Fido2VerificationException or JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            logger.LogWarning("A passkey did not register: {Reason}.", exception.Message);
            return null;
        }
    }

    /// <summary>Options to sign with one of <paramref name="allowed"/>, or with any discoverable passkey of this tenant when it is empty.</summary>
    public string AssertionOptions(IReadOnlyList<Factor> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        var options = Library().GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [.. allowed.Where(f => f.IsPasskey).Select(f => new PublicKeyCredentialDescriptor(f.CredentialId!))],
            UserVerification = UserVerificationRequirement.Required,
        });
        return options.ToJson();
    }

    /// <summary>The credential id an assertion names, before it is verified, to find the passkey it claims to be.</summary>
    public static byte[]? CredentialIdOf(JsonElement credential)
    {
        try
        {
            return credential.Deserialize<AuthenticatorAssertionRawResponse>()?.RawId;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Verifies an assertion of <paramref name="factor"/> against <paramref name="optionsJson"/>; the new counter, or null.</summary>
    public async Task<long?> VerifyAsync(string optionsJson, JsonElement credential, Factor factor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factor);
        try
        {
            var response = credential.Deserialize<AuthenticatorAssertionRawResponse>()
                ?? throw new JsonException("The credential is empty.");
            var verified = await Library().MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = response,
                    OriginalOptions = Fido2NetLib.AssertionOptions.FromJson(optionsJson),
                    StoredPublicKey = factor.PublicKey!,
                    StoredSignatureCounter = (uint)factor.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = (owner, _) => Task.FromResult(
                        owner.CredentialId.AsSpan().SequenceEqual(factor.CredentialId) && (owner.UserHandle is null || owner.UserHandle.AsSpan().SequenceEqual(factor.UserHandle))),
                },
                cancellationToken).ConfigureAwait(false);
            return verified.SignCount;
        }
        catch (Exception exception) when (exception is Fido2VerificationException or JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            logger.LogWarning("A passkey of principal {PrincipalId} did not verify: {Reason}.", factor.PrincipalId, exception.Message);
            return null;
        }
    }

    /// <summary>The library for this request: the relying party is the hostname asked, when it is one of the tenant's.</summary>
    private Fido2 Library()
    {
        var host = accessor.HttpContext?.Request.Host.Host;
        var relyingParty = tenant.ServesHost(host) ? TenantContext.NormaliseHost(host) : tenant.PrimaryHostname;
        var configuration = new Fido2Configuration
        {
            RPID = relyingParty,
            RPName = RelyingPartyName,
            Origins = new HashSet<string>(tenant.Hostnames.Select(hostname => $"https://{hostname}"), StringComparer.Ordinal),
        };
        return new Fido2(configuration, null!);
    }
}
