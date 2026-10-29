using Iron.Kernel.Authorization.Contracts;

namespace Iron.Kernel.Authorization.Services;

/// <summary>The acting the permission checks follow, for the modules that must follow it too (ADR-0037, ADR-0057).</summary>
public sealed class ActingFor(ActingScope acting) : IActingFor
{
    public async Task<Guid?> DelegatorOfAsync(Guid principalId, CancellationToken cancellationToken) =>
        (await acting.ForAsync(principalId, cancellationToken).ConfigureAwait(false))?.DelegatorId;
}
