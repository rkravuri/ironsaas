using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// The smoke's steps for S3 T9b (ADR-0054). <see cref="TlsAsync"/> speaks to the ingress with ClientHellos written byte by
/// byte, so the client's own TLS library cannot refuse first: TLS 1.0 is refused with a protocol version alert, TLS 1.2
/// with only old suites with a handshake failure, and TLS 1.2 with modern suites is answered. <see cref="RotateIndexKeyAsync"/>
/// stores a key check, has the operator rotate the tenant's index key, waits until the unit has hashed every value again
/// and the old version is retired, and finds the check by its exact value and not by a part of it.
/// </summary>
public sealed class KeySteps(Api api, SmokeOptions options, Func<CancellationToken, Task<string>> signInAdministrator, string run)
{
    private const byte Handshake = 0x16;
    private const byte AlertRecord = 0x15;
    private const byte ProtocolVersion = 70;
    private const byte HandshakeFailure = 40;
    private static readonly TimeSpan RotationWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan UnitWait = TimeSpan.FromMinutes(1);

    // TLS 1.2's suites the ingress offers: forward-secret AEAD only.
    private static readonly ushort[] ModernSuites = [0xC02B, 0xC02F, 0xC02C, 0xC030, 0xCCA9, 0xCCA8];

    // Suites without forward secrecy or with CBC and SHA-1: RSA key exchange, 3DES, ECDHE with CBC.
    private static readonly ushort[] OldSuites = [0x002F, 0x0035, 0x000A, 0xC013, 0xC014, 0x009C];

    public async Task<string> TlsAsync(CancellationToken cancellationToken)
    {
        var tls10 = await HelloAsync(0x0301, ModernSuites.Concat(OldSuites).ToArray(), cancellationToken).ConfigureAwait(false);
        if (tls10 != (AlertRecord, ProtocolVersion))
        {
            throw new SmokeException($"a TLS 1.0 client was answered {Describe(tls10)}, not refused with a protocol version alert");
        }

        var old = await HelloAsync(0x0303, OldSuites, cancellationToken).ConfigureAwait(false);
        if (old != (AlertRecord, HandshakeFailure))
        {
            throw new SmokeException($"a TLS 1.2 client of old suites was answered {Describe(old)}, not refused with a handshake failure");
        }

        var modern = await HelloAsync(0x0303, ModernSuites, cancellationToken).ConfigureAwait(false);
        if (modern != (Handshake, 2))
        {
            throw new SmokeException($"a TLS 1.2 client of modern suites was answered {Describe(modern)}, not with a server hello");
        }

        return "the ingress refused TLS 1.0 (protocol version) and TLS 1.2 with old suites (handshake failure), and answered TLS 1.2 with modern suites";
    }

    public async Task<string> RotateIndexKeyAsync(CancellationToken cancellationToken)
    {
        var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
        var value = $"SM-{run}-{RandomNumberGenerator.GetInt32(100_000, 999_999)}";
        (await TenantAsync(HttpMethod.Post, "/api/v1/keys/checks", admin, new { label = $"Smoke run {run}", value }, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.Created, "the key check stored");
        if ((await FindAsync(admin, value, cancellationToken).ConfigureAwait(false)).Count != 1)
        {
            throw new SmokeException("the key check is not found by its exact value before the rotation");
        }

        // The rotation needs a fresh token (ADR-0054): one asked for now.
        var token = (await api.SendAsync(HttpMethod.Post, options.ControlUrl, "/connect/token", basic: (options.OperatorClientId, options.OperatorSecret), form: "grant_type=client_credentials", cancellationToken: cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.OK, "a fresh operator token").Text("access_token");
        var reason = $"Smoke run {run}: the index key rotates and its sample value is still found";
        var asked = await api.SendAsync(HttpMethod.Post, options.ControlUrl, $"/api/v1/tenants/{options.Tenant}/keys/index/rotations", bearer: token, json: new { reason }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (asked.Status == HttpStatusCode.Conflict)
        {
            // An earlier run's rotation is still going: it is waited for, then this run's is asked for.
            await WaitForOpenRotationsAsync(token!, cancellationToken).ConfigureAwait(false);
            asked = await api.SendAsync(HttpMethod.Post, options.ControlUrl, $"/api/v1/tenants/{options.Tenant}/keys/index/rotations", bearer: token, json: new { reason }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var id = asked.Expect(HttpStatusCode.Accepted, "the index key's rotation").Text("id");
        var rotation = await RetiredAsync(token!, id!, cancellationToken).ConfigureAwait(false);
        var to = rotation.GetProperty("to_version").GetInt32();

        // The unit hears of the retirement at its next poll of the control plane, every 15 seconds.
        var heard = await UnitReadsOnlyAsync(admin, to, cancellationToken).ConfigureAwait(false);

        var found = await FindAsync(admin, value, cancellationToken).ConfigureAwait(false);
        if (found.Count != 1 || found[0].GetProperty("value").GetString() != value)
        {
            throw new SmokeException($"after the rotation the key check is found {found.Count} time(s) by its exact value, not once");
        }

        if ((await FindAsync(admin, value[..^3], cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            throw new SmokeException("a part of the key check's value found it");
        }

        return $"index key rotated from version {rotation.GetProperty("from_version").GetInt32()} to {to}, every hash made again ({rotation.GetProperty("done").GetInt64()} values) and the old version retired, which the unit read after {heard.TotalSeconds:0} s; the sample value found by its exact value, a part of it not";
    }

    private async Task<TimeSpan> UnitReadsOnlyAsync(string admin, int version, CancellationToken cancellationToken)
    {
        var started = TimeProvider.System.GetUtcNow();
        var deadline = started + UnitWait;
        while (true)
        {
            var keys = (await TenantAsync(HttpMethod.Get, "/api/v1/keys", admin, null, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the unit's keys");
            var index = keys.Body!.Value.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("key").GetString() == "index");
            var (latest, minimum) = (index.GetProperty("version").GetInt32(), index.GetProperty("minimum_version").GetInt32());
            if (latest == version && minimum == version)
            {
                return TimeProvider.System.GetUtcNow() - started;
            }

            if (TimeProvider.System.GetUtcNow() >= deadline)
            {
                throw new SmokeException($"the unit reads its index key at version {latest} from {minimum} after {UnitWait.TotalSeconds:0} s, not {version} alone");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<JsonElement>> FindAsync(string admin, string value, CancellationToken cancellationToken)
    {
        var answer = (await TenantAsync(HttpMethod.Post, "/api/v1/keys/checks/search", admin, new { value }, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the key checks searched");
        return [.. answer.Body!.Value.GetProperty("matches").EnumerateArray()];
    }

    private async Task<JsonElement> RetiredAsync(string token, string id, CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + RotationWait;
        string? state = null;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var rotation = (await api.SendAsync(HttpMethod.Get, options.ControlUrl, $"/api/v1/tenants/{options.Tenant}/keys/rotations/{id}", bearer: token, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the rotation");
            state = rotation.Text("state");
            if (state == "retired")
            {
                return rotation.Body!.Value.Clone();
            }

            if (state == "failed")
            {
                throw new SmokeException($"the rotation failed: {rotation.Text("error")}");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }

        throw new SmokeException($"the rotation stood {state} after {RotationWait.TotalMinutes} minutes: is the fleet up, and the unit's worker?");
    }

    private async Task WaitForOpenRotationsAsync(string token, CancellationToken cancellationToken)
    {
        var keys = (await api.SendAsync(HttpMethod.Get, options.ControlUrl, $"/api/v1/tenants/{options.Tenant}/keys", bearer: token, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the tenant's keys");
        foreach (var open in keys.Body!.Value.GetProperty("rotations").EnumerateArray().Where(r => r.GetProperty("key").GetString() == "index" && r.GetProperty("state").GetString() is "requested" or "rotated" or "finished"))
        {
            await RetiredAsync(token, open.GetProperty("id").GetString()!, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<Answer> TenantAsync(HttpMethod method, string path, string cookie, object? json, CancellationToken cancellationToken) =>
        api.SendAsync(method, options.IngressUrl, path, host: options.Hostname, cookie: cookie, json: json, cancellationToken: cancellationToken);

    /// <summary>Sends one ClientHello to the ingress's TLS port and answers the first record's type and its first byte that matters.</summary>
    private async Task<(byte Record, byte What)> HelloAsync(ushort version, ushort[] suites, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(options.IngressUrl.Host, 443, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync(ClientHello(version, suites, options.Hostname), cancellationToken).ConfigureAwait(false);
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var body = new byte[Math.Min((int)BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3)), 4096)];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

        // An alert is a level and a description; a handshake record starts with its message type (2: server hello).
        return header[0] == AlertRecord ? (AlertRecord, body[1]) : (header[0], body[0]);
    }

    private static string Describe((byte Record, byte What) answer) => answer.Record switch
    {
        AlertRecord => $"with alert {answer.What}",
        Handshake => $"with handshake message {answer.What}",
        _ => $"with a record of type {answer.Record}",
    };

    /// <summary>A ClientHello (RFC 5246 section 7.4.1.2) of one version and the given suites, with the extensions a TLS 1.2 server needs.</summary>
    public static byte[] ClientHello(ushort version, IReadOnlyList<ushort> suites, string serverName)
    {
        ArgumentNullException.ThrowIfNull(suites);
        var name = Encoding.ASCII.GetBytes(serverName);
        var extensions = new List<byte>();
        Extension(extensions, 0x0000, [.. U16(name.Length + 3), 0x00, .. U16(name.Length), .. name]);
        Extension(extensions, 0x000a, [.. U16(6), 0x00, 0x1d, 0x00, 0x17, 0x00, 0x18]);
        Extension(extensions, 0x000b, [0x01, 0x00]);
        if (version >= 0x0303)
        {
            Extension(extensions, 0x000d, [.. U16(12), 0x04, 0x03, 0x08, 0x04, 0x04, 0x01, 0x05, 0x03, 0x08, 0x05, 0x05, 0x01]);
        }

        var hello = new List<byte>();
        hello.AddRange(U16(version));
        hello.AddRange(RandomNumberGenerator.GetBytes(32));
        hello.Add(0x00);
        hello.AddRange(U16(suites.Count * 2));
        foreach (var suite in suites)
        {
            hello.AddRange(U16(suite));
        }

        hello.AddRange([0x01, 0x00]);
        hello.AddRange(U16(extensions.Count));
        hello.AddRange(extensions);

        var message = new List<byte> { 0x01, (byte)(hello.Count >> 16), (byte)(hello.Count >> 8), (byte)hello.Count };
        message.AddRange(hello);
        var record = new List<byte> { Handshake, 0x03, 0x01 };
        record.AddRange(U16(message.Count));
        record.AddRange(message);
        return [.. record];
    }

    private static void Extension(List<byte> extensions, ushort type, byte[] data)
    {
        extensions.AddRange(U16(type));
        extensions.AddRange(U16(data.Length));
        extensions.AddRange(data);
    }

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];
}
