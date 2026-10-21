namespace Iron.Kernel.Authorization.Contracts;

/// <summary>
/// The tenant's administrators for the whole tenant, for the guards that keep one (A2, T2): removing the role and
/// deactivating a person both refuse to leave the tenant without an active tenant administrator. Identity owns the
/// status and asks here who holds the role.
/// </summary>
public interface ITenantAdministrators
{
    /// <summary>
    /// Takes the administrators' lock for the caller's transaction, which must be open, so two changes cannot each see
    /// the other's administrator and leave none; then answers who holds the tenant administrator role tenant-wide.
    /// </summary>
    Task<IReadOnlyList<Guid>> LockAndListAsync(CancellationToken cancellationToken);

    /// <summary>Whether the principal holds the tenant administrator role for the whole tenant, without a lock.</summary>
    Task<bool> IsTenantAdministratorAsync(Guid principalId, CancellationToken cancellationToken);
}
