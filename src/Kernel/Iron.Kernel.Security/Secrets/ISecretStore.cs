namespace Iron.Kernel.Security.Secrets;

/// <summary>
/// The secrets store behind one interface (TF-073): OpenBao on the laptop and in the first environments, Key Vault later.
/// A unit reads its own path only; the fleet writes it when it creates the unit (T14). Values are never logged.
/// </summary>
public interface ISecretStore
{
    /// <summary>The key-value pairs at a path relative to the mount, or null when there is nothing there.</summary>
    Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes (replaces) the key-value pairs at a path.</summary>
    Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);

    /// <summary>True when the store answers and is unsealed.</summary>
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes the pairs at a path only if nothing was ever written there, and has the store delete them after
    /// <paramref name="lifetime"/>: a one-time place for something handed over (ADR-0046).
    /// </summary>
    Task WriteOnceAsync(string path, IReadOnlyDictionary<string, string> values, TimeSpan lifetime, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store has no one-time places.");

    /// <summary>Deletes a path with every version it had, so nothing can bring it back.</summary>
    Task DeleteAsync(string path, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store deletes nothing.");
}

/// <summary>
/// What the fleet needs beyond reading and writing: a policy per tenant and a unit token limited to it (TF-073, TF-210).
/// OpenBao implements it; Key Vault has access policies and managed identities instead and gets its own fleet step.
/// </summary>
public interface ISecretStoreAdmin
{
    /// <summary>Creates or replaces a policy that allows reading the given paths (relative to the mount) and nothing else.</summary>
    Task CreateReadPolicyAsync(string policyName, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>
    /// Creates or replaces a policy that allows writing new values at the paths matching a pattern (relative to the mount,
    /// <c>*</c> for the last segment) and nothing else, not even reading them back: a place to deposit, for ADR-0046.
    /// </summary>
    Task CreateDepositPolicyAsync(string policyName, string pathPattern, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store has no deposit policies.");

    /// <summary>Creates a token that carries only the given policies. The caller stores it; the store cannot show it again.</summary>
    Task<string> CreateTokenAsync(IReadOnlyList<string> policies, string displayName, TimeSpan timeToLive, CancellationToken cancellationToken);
}

/// <summary>Bound from the <c>Secrets</c> configuration section: the only secret-related settings a unit's environment holds.</summary>
public sealed class SecretStoreOptions
{
    public const string SectionName = "Secrets";
    public const string UnitSecretName = "unit";

    /// <summary>The store address, for example <c>http://openbao:8200</c>. Empty means no store: configuration comes from the environment (laptop, tests).</summary>
    public string? Address { get; set; }

    /// <summary>The unit token, scoped by policy to the unit's path.</summary>
    public string? Token { get; set; }

    /// <summary>The KV version 2 mount, <c>secret</c> by default.</summary>
    public string Mount { get; set; } = "secret";

    /// <summary>The unit's path under the mount: <c>tenants/acme</c>. Its <c>unit</c> secret holds the configuration keys.</summary>
    public string? Path { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(Token);

    public string UnitSecretPath => $"{Path?.Trim('/')}/{UnitSecretName}";
}
