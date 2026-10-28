using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iron.Kernel.Protection.Contracts;

namespace Iron.Kernel.Protection.Services;

/// <summary>
/// The login's proof of work (TF-072, ADR-0053), ALTCHA's protocol (MIT) written here so nothing is loaded from another
/// host and the content security policy stays strict. The server picks a secret number up to a maximum, sends the SHA-256
/// of a salt and that number, signed with its key; the client finds the number by trying each in turn and sends it back
/// with the challenge. The answer is checked once: the signature, the hash, the salt's expiry, and that it was not used
/// before. Failures are counted per address and per identifier in this process; S6 moves them to a shared store.
/// </summary>
public sealed class LoginChallenges : ILoginChallenges
{
    public const string Algorithm = "SHA-256";
    private const int MaxRemembered = 10_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ProtectionOptions.ChallengeOptions _options;
    private readonly TimeProvider _time;
    private readonly ProtectionMetrics _metrics;
    private readonly byte[] _key;
    private readonly ConcurrentDictionary<string, FailureWindow> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    public LoginChallenges(ProtectionOptions options, TimeProvider time, ProtectionMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Challenge;
        _time = time;
        _metrics = metrics;
        _key = string.IsNullOrWhiteSpace(_options.Key) ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(_options.Key);
    }

    private TimeSpan Window => TimeSpan.FromMinutes(Math.Max(1, _options.WindowMinutes));

    public bool IsRequired(IPAddress? address, string loginId)
    {
        var now = _time.GetUtcNow();
        return Count(AddressKey(address), now) >= _options.AfterFailures || Count(LoginKey(loginId), now) >= _options.AfterFailures;
    }

    public LoginChallenge Issue(IPAddress? address)
    {
        var now = _time.GetUtcNow();
        var expires = now.AddMinutes(Math.Max(1, _options.LifetimeMinutes)).ToUnixTimeSeconds();
        var salt = string.Create(CultureInfo.InvariantCulture, $"{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12))}?expires={expires}");

        // The progressive delay: each failure past the threshold doubles the work, up to sixteen times.
        var beyond = Math.Clamp(Count(AddressKey(address), now) - _options.AfterFailures, 0, 4);
        var maxNumber = Math.Min(int.MaxValue - 1L, Math.Max(1, _options.MaxNumber) << beyond);
        var number = RandomNumberGenerator.GetInt32(0, (int)maxNumber + 1);
        var challenge = Hash(salt, number);
        _metrics.Challenge("issued");
        return new LoginChallenge(Algorithm, challenge, maxNumber, salt, Sign(challenge));
    }

    public bool Verify(string? solution)
    {
        if (Read(solution) is not { } answer || !Valid(answer) || !_used.TryAdd(answer.Signature, ExpiryOf(answer.Salt)!.Value))
        {
            _metrics.Challenge("refused");
            return false;
        }

        Forget();
        _metrics.Challenge("solved");
        return true;
    }

    public void Failed(IPAddress? address, string loginId)
    {
        var now = _time.GetUtcNow();
        Add(AddressKey(address), now);
        Add(LoginKey(loginId), now);
    }

    public void Succeeded(string loginId) => _failures.TryRemove(LoginKey(loginId), out _);

    /// <summary>The hex SHA-256 of the salt followed by the number, as the client computes it.</summary>
    public static string Hash(string salt, long number) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + number.ToString(CultureInfo.InvariantCulture))));

    /// <summary>The answer a client sends: the challenge with its number, as JSON in base64 (ALTCHA's payload).</summary>
    public static string Payload(LoginChallenge challenge, long number)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var json = JsonSerializer.Serialize(new Answer(challenge.Algorithm, challenge.Challenge, number, challenge.Salt, challenge.Signature), Json);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Finds the number by trying each in turn, as the shell does; for the smoke and the tests.</summary>
    public static long Solve(LoginChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        for (long number = 0; number <= challenge.MaxNumber; number++)
        {
            if (Hash(challenge.Salt, number) == challenge.Challenge)
            {
                return number;
            }
        }

        throw new InvalidOperationException("The challenge has no answer up to its maximum.");
    }

    private string Sign(string challenge) => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(challenge)));

    private bool Valid(Answer answer)
    {
        if (answer.Algorithm != Algorithm || ExpiryOf(answer.Salt) is not { } expires || expires <= _time.GetUtcNow() || answer.Number < 0)
        {
            return false;
        }

        var signed = Encoding.ASCII.GetBytes(Sign(answer.Challenge));
        return CryptographicOperations.FixedTimeEquals(signed, Encoding.ASCII.GetBytes(answer.Signature))
            && string.Equals(Hash(answer.Salt, answer.Number), answer.Challenge, StringComparison.Ordinal);
    }

    private static Answer? Read(string? solution)
    {
        if (string.IsNullOrWhiteSpace(solution) || solution.Length > 2000)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Answer>(Convert.FromBase64String(solution), Json) is { Challenge: not null, Salt: not null, Signature: not null } answer ? answer : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ExpiryOf(string salt)
    {
        var query = salt.IndexOf('?', StringComparison.Ordinal);
        if (query < 0)
        {
            return null;
        }

        foreach (var part in salt[(query + 1)..].Split('&'))
        {
            if (part.StartsWith("expires=", StringComparison.Ordinal) && long.TryParse(part["expires=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        return null;
    }

    private static string AddressKey(IPAddress? address) => $"address:{address?.ToString() ?? "unknown"}";

    private static string LoginKey(string loginId) => $"login:{loginId}";

    private int Count(string key, DateTimeOffset now) =>
        _failures.TryGetValue(key, out var window) && now - window.Started < Window ? window.Count : 0;

    private void Add(string key, DateTimeOffset now)
    {
        if (_failures.Count > MaxRemembered)
        {
            foreach (var (stale, window) in _failures)
            {
                if (now - window.Started >= Window)
                {
                    _failures.TryRemove(stale, out _);
                }
            }
        }

        _failures.AddOrUpdate(key, _ => new FailureWindow(now, 1), (_, window) => now - window.Started >= Window ? new FailureWindow(now, 1) : window with { Count = window.Count + 1 });
    }

    /// <summary>Used answers are kept until their challenge expires, then forgotten.</summary>
    private void Forget()
    {
        if (_used.Count <= MaxRemembered / 10)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var (signature, expires) in _used)
        {
            if (expires <= now)
            {
                _used.TryRemove(signature, out _);
            }
        }
    }

    private sealed record FailureWindow(DateTimeOffset Started, int Count);

    private sealed record Answer(
        [property: JsonPropertyName("algorithm")] string Algorithm,
        [property: JsonPropertyName("challenge")] string Challenge,
        [property: JsonPropertyName("number")] long Number,
        [property: JsonPropertyName("salt")] string Salt,
        [property: JsonPropertyName("signature")] string Signature);
}
