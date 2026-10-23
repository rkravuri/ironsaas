namespace Iron.Kernel.Identity.Contracts;

/// <summary>
/// Service accounts another module makes for the platform's own clients (ADR-0050): the fleet gives each tenant unit a
/// client of its own in the control plane, which sends that unit's audit anchors and nothing else.
/// </summary>
public interface IServiceAccounts
{
    /// <summary>
    /// The service account with this client id: created with the role and the secret, or given the secret when it holds
    /// another, and the role when it lacks it. Safe to call again. Answers the account's principal id.
    /// </summary>
    Task<Result<Guid>> EnsureAsync(string clientId, string displayName, string roleCode, string secret, CancellationToken cancellationToken);
}
