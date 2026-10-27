using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Audit.Services;

/// <summary>Where the sealing worker sends the anchors after each pass: the control plane for a unit, a store for the control plane.</summary>
public interface IAuditAnchorSink
{
    /// <summary>Sends the anchors not sent yet and answers how many went.</summary>
    Task<int> SendAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The control plane's anchoring (TF-061, ADR-0053): each seal's position and hash go to the host's anchor store, outside
/// its database, which keeps what it has. At the first pass of a process it asks the store how far it holds; then it
/// keeps count itself. Without a store, as on a host run without one configured, nothing is anchored and the log says so
/// once. A store it cannot reach is tried again at the next pass.
/// </summary>
public sealed class AuditAnchorKeeper(AuditDbContext context, IServiceProvider services, AnchorProgress progress, ILogger<AuditAnchorKeeper> logger) : IAuditAnchorSink
{
    private static int _toldNoStore;

    public async Task<int> SendAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IAuditAnchorStore>() is not { } store)
        {
            if (Interlocked.Exchange(ref _toldNoStore, 1) == 0)
            {
                logger.LogWarning("No audit anchor store is configured (ControlAudit:AnchorsSasUrl): this host's chain is sealed but not anchored.");
            }

            return 0;
        }

        try
        {
            progress.SealedUpTo ??= await store.LatestSealAsync(cancellationToken).ConfigureAwait(false);
            var seals = await context.Seals.AsNoTracking()
                .Where(s => s.LastPosition > progress.SealedUpTo)
                .OrderBy(s => s.LastPosition)
                .Take(100)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var sent = 0;
            foreach (var seal in seals)
            {
                var anchor = new AuditAnchor(AuditAnchorKinds.Seal, seal.LastPosition, seal.LastHash, seal.CreatedAt);
                if (await store.AppendAsync(anchor, cancellationToken).ConfigureAwait(false) == AuditAnchorAppend.Conflict)
                {
                    logger.LogError("ALERT: the anchor store holds another hash for audit position {Position}; the anchor there stands and the verification reports it.", seal.LastPosition);
                }

                progress.SealedUpTo = seal.LastPosition;
                sent++;
            }

            return sent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AuditMetrics.AnchorFailures.Add(1);
            logger.LogWarning("The anchor store could not take the audit anchors; they are sent at the next pass: {Message}", exception.Message);
            return 0;
        }
    }
}
