using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// An authenticator app, as the smoke holds one (RFC 6238: HMAC-SHA1, six digits, 30-second steps, ADR-0047). The unit
/// accepts a code once, of a step later than the last it accepted, and one step either side of its clock; so each code
/// this object gives is of a later step than the one before, the next step ahead when the current one is used, and it
/// waits for the clock when both are.
/// </summary>
public sealed class Authenticator(string secret)
{
    private const long StepSeconds = 30;
    private readonly byte[] _key = FromBase32(secret);
    private long _last = -1;

    /// <summary>The key, as the unit gave it at enrolment; the smoke keeps it in the store beside the password.</summary>
    public string Secret => secret;

    public async Task<string> NextCodeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
            var current = now / StepSeconds;
            var step = Math.Max(current, _last + 1);
            if (step <= current + 1)
            {
                _last = step;
                return Code(step);
            }

            await Task.Delay(TimeSpan.FromSeconds(StepSeconds - (now % StepSeconds) + 1), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A code that is surely wrong now: one no step within the unit's tolerance gives, so it matches none by chance.</summary>
    public string WrongCode()
    {
        var current = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() / StepSeconds;
        var taken = new HashSet<string>(Enumerable.Range(-1, 3).Select(offset => Code(current + offset)), StringComparer.Ordinal);
        var wrong = 0;
        while (taken.Contains(wrong.ToString("D6", CultureInfo.InvariantCulture)))
        {
            wrong++;
        }

        return wrong.ToString("D6", CultureInfo.InvariantCulture);
    }

    private string Code(long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(_key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] FromBase32(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var character in text.ToUpperInvariant().Where(c => c is not (' ' or '=' or '-')))
        {
            buffer = ((buffer << 5) | Alphabet.IndexOf(character, StringComparison.Ordinal)) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}

/// <summary>
/// Signing in as the shell does (TF-011, TF-013, ADR-0047): the password, then the second step when the answer asks for
/// it, with the authenticator's code. A code another process used in the same step is refused as any wrong code is; the
/// smoke then takes the next step's code, at most twice, which stays under the three that start a cooling period.
/// </summary>
public sealed class SignIn(Api api, SmokeOptions options)
{
    /// <summary>What the password step asked for: <c>second_factor</c>, <c>enrolment</c>, or null for a session.</summary>
    public static string? StepOf(Answer answer) =>
        answer.Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("step", out var step) && step.ValueKind == JsonValueKind.Object
            ? step.GetProperty("kind").GetString()
            : null;

    public Task<Answer> PasswordAsync(string loginId, string password, CancellationToken cancellationToken) =>
        api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/login", host: options.Hostname, json: new { login_id = loginId, password }, cancellationToken: cancellationToken);

    /// <summary>The session cookie of a full sign-in.</summary>
    public async Task<string> AsAsync(string loginId, string password, Authenticator? authenticator, CancellationToken cancellationToken)
    {
        var answer = (await PasswordAsync(loginId, password, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, $"sign in as {loginId}");
        var step = StepOf(answer);
        if (step is null)
        {
            return answer.SetCookie ?? throw new SmokeException($"signing in as {loginId} answered 200 without a session cookie");
        }

        if (step != "second_factor" || authenticator is null)
        {
            throw new SmokeException($"signing in as {loginId} asks for {step}, which the smoke has no factor for");
        }

        return await SecondFactorAsync(loginId, answer.SetCookie ?? throw new SmokeException("the second step came without its cookie"), authenticator, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The second step with the authenticator's code; the session cookie.</summary>
    public async Task<string> SecondFactorAsync(string loginId, string signInCookie, Authenticator authenticator, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var code = await authenticator.NextCodeAsync(cancellationToken).ConfigureAwait(false);
            var proved = await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/second-factor", host: options.Hostname, cookie: signInCookie, json: new { code }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (proved.Status == HttpStatusCode.OK)
            {
                return proved.SetCookie ?? throw new SmokeException($"the second step of {loginId} answered 200 without a session cookie");
            }

            if (attempt >= 3 || proved.Text("error_code") != "identity.second_factor.invalid")
            {
                proved.Expect(HttpStatusCode.OK, $"the second factor of {loginId}");
            }
        }
    }

    /// <summary>Enrols an authenticator app at sign-in, as a person who must have a factor does; the session and the app.</summary>
    public async Task<(string Session, Authenticator App, int RecoveryCodes)> EnrolAtSignInAsync(string signInCookie, CancellationToken cancellationToken)
    {
        var setup = (await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/enrolment/authenticator-app", host: options.Hostname, cookie: signInCookie, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.OK, "start an authenticator app at sign-in");
        var app = new Authenticator(setup.Text("secret") ?? throw new SmokeException("the authenticator app came without its key"));
        var confirmed = (await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/enrolment/authenticator-app/confirm", host: options.Hostname, cookie: signInCookie, json: new { code = await app.NextCodeAsync(cancellationToken).ConfigureAwait(false), name = "Smoke" }, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.OK, "confirm the authenticator app");
        var codes = confirmed.Body!.Value.TryGetProperty("recovery_codes", out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
        return (confirmed.SetCookie ?? throw new SmokeException("the enrolment answered 200 without a session cookie"), app, codes);
    }

    public async Task SignOutAsync(string cookie, CancellationToken cancellationToken)
    {
        var answer = await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/logout", host: options.Hostname, cookie: cookie, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (answer.Status is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
        {
            answer.Expect(HttpStatusCode.NoContent, "sign out");
        }
    }
}
