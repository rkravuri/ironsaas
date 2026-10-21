using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// A passkey in software, for the tests that run without a browser: one P-256 key that answers a WebAuthn creation with
/// "none" attestation and an assertion with its signature, user present and user verified, and a counter that grows. It
/// writes the JSON a browser's <c>toJSON()</c> gives, so the API reads it as it reads a real authenticator's. The browser
/// tests use Chromium's virtual authenticator instead.
/// </summary>
public sealed class SoftwareAuthenticator : IDisposable
{
    public const string Origin = "https://acme.localhost";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private uint _counter;

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

    public byte[]? UserHandle { get; private set; }

    /// <summary>Answers creation options as an authenticator that verified its user does.</summary>
    public JsonElement Create(JsonElement options)
    {
        var challenge = options.GetProperty("challenge").GetString()!;
        var rpId = options.GetProperty("rp").GetProperty("id").GetString()!;
        UserHandle = Base64Url(options.GetProperty("user").GetProperty("id").GetString()!);
        var clientData = ClientData("webauthn.create", challenge);
        var authenticatorData = AuthenticatorData(rpId, flags: 0x45, attested: true);

        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt");
        attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt");
        attestation.WriteStartMap(0);
        attestation.WriteEndMap();
        attestation.WriteTextString("authData");
        attestation.WriteByteString(authenticatorData);
        attestation.WriteEndMap();

        return Json(new JsonObject
        {
            ["id"] = Base64Url(CredentialId),
            ["rawId"] = Base64Url(CredentialId),
            ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url(clientData),
                ["attestationObject"] = Base64Url(attestation.Encode()),
                ["transports"] = new JsonArray("internal"),
            },
            ["clientExtensionResults"] = new JsonObject(),
        });
    }

    /// <summary>Answers assertion options with this passkey, the counter one higher than before.</summary>
    public JsonElement Get(JsonElement options, string origin = Origin)
    {
        var challenge = options.GetProperty("challenge").GetString()!;
        var rpId = options.GetProperty("rpId").GetString()!;
        _counter++;
        var clientData = ClientData("webauthn.get", challenge, origin);
        var authenticatorData = AuthenticatorData(rpId, flags: 0x05, attested: false);
        var signed = authenticatorData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return Json(new JsonObject
        {
            ["id"] = Base64Url(CredentialId),
            ["rawId"] = Base64Url(CredentialId),
            ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url(clientData),
                ["authenticatorData"] = Base64Url(authenticatorData),
                ["signature"] = Base64Url(signature),
                ["userHandle"] = UserHandle is null ? null : Base64Url(UserHandle),
            },
            ["clientExtensionResults"] = new JsonObject(),
        });
    }

    public void Dispose() => _key.Dispose();

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    private static byte[] ClientData(string type, string challenge, string origin = Origin) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object> { ["type"] = type, ["challenge"] = challenge, ["origin"] = origin, ["crossOrigin"] = false }));

    /// <summary>The relying party's hash, the flags, the counter, and for a creation the credential and its COSE key.</summary>
    private byte[] AuthenticatorData(string rpId, byte flags, bool attested)
    {
        var data = new List<byte>();
        data.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        data.Add(flags);
        data.AddRange([(byte)(_counter >> 24), (byte)(_counter >> 16), (byte)(_counter >> 8), (byte)_counter]);
        if (attested)
        {
            data.AddRange(new byte[16]); // no AAGUID: "none" attestation names no model
            data.AddRange([(byte)(CredentialId.Length >> 8), (byte)CredentialId.Length]);
            data.AddRange(CredentialId);
            data.AddRange(CoseKey());
        }

        return [.. data];
    }

    private byte[] CoseKey()
    {
        var point = _key.ExportParameters(includePrivateParameters: false).Q;
        var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cose.WriteStartMap(5);
        cose.WriteInt32(1);
        cose.WriteInt32(2); // kty: EC2
        cose.WriteInt32(3);
        cose.WriteInt32(-7); // alg: ES256
        cose.WriteInt32(-1);
        cose.WriteInt32(1); // crv: P-256
        cose.WriteInt32(-2);
        cose.WriteByteString(point.X!);
        cose.WriteInt32(-3);
        cose.WriteByteString(point.Y!);
        cose.WriteEndMap();
        return cose.Encode();
    }

    private static JsonElement Json(JsonObject node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
}
