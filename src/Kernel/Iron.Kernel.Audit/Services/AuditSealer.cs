using System.Diagnostics.Metrics;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Audit.Services;

/// <summary>
/// Gives committed audit events their places in the tenant's chain (TF-061, ADR-0050). Events are written as they always
/// were, by many transactions at once; a pass of the sealer takes the chain's lock, finds the events nobody has sealed,
/// oldest first, and gives each the next position, the previous event's hash and its own, through
/// <c>kernel_audit.seal_event</c>, the one way the chain columns are ever set. Positions come from the chain's head, not a
/// sequence, so they are consecutive: a gap later always means a deleted event. Content never changes.
/// </summary>
public sealed class AuditSealer(AuditDbContext context, IUnitOfWork unitOfWork, TimeProvider time)
{
    /// <summary>At most this many events in one pass, so a pass stays a short transaction.</summary>
    public const int BatchSize = 500;

    /// <summary>The chain's lock: one sealer at a time in the tenant's database, whichever process runs it.</summary>
    public const long LockKey = 6_100_610_000_061;

    /// <summary>One pass: the seal it made, or null when nothing waited.</summary>
    public async Task<AuditSeal?> SealAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("select pg_advisory_xact_lock({0})", [LockKey], cancellationToken).ConfigureAwait(false);

        var pending = await context.Events.AsNoTracking()
            .Where(e => e.ChainPosition == null)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return null;
        }

        var head = await HeadAsync(context, cancellationToken).ConfigureAwait(false);
        var position = head?.Position ?? 0;
        var previous = head?.Hash ?? AuditCanonicalForm.Genesis;
        var first = position + 1;
        var now = time.GetUtcNow();
        foreach (var record in pending)
        {
            position++;
            var hash = AuditCanonicalForm.Hash(record.Chained(position), previous);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"select kernel_audit.seal_event({record.Id}, {position}, {previous}, {hash}, {AuditCanonicalForm.Version}, {now})",
                cancellationToken).ConfigureAwait(false);
            previous = hash;
        }

        var seal = new AuditSeal(first, position, previous);
        context.Seals.Add(seal);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        AuditMetrics.Sealed.Add(pending.Count);
        return seal;
    }

    /// <summary>The chain's head: the latest seal's position and hash, or null before the first.</summary>
    public static async Task<ChainHead?> HeadAsync(AuditDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var latest = await context.Seals.AsNoTracking()
            .OrderByDescending(s => s.LastPosition)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return latest is null ? null : new ChainHead(latest.LastPosition, latest.LastHash, latest.CreatedAt);
    }
}

/// <summary>The audit trail's own measures (ADR-0050), exported with every <c>Iron.*</c> meter.</summary>
public static class AuditMetrics
{
    public static readonly Meter Meter = new("Iron.Audit");

    /// <summary>Events the sealer gave a place in the chain.</summary>
    public static readonly Counter<long> Sealed = Meter.CreateCounter<long>("audit.events_sealed");

    /// <summary>Anchors the control plane did not take; the next pass sends them again.</summary>
    public static readonly Counter<long> AnchorFailures = Meter.CreateCounter<long>("audit.anchor_failures");
}

/// <summary>How often the worker seals (<c>Audit:Sealing</c>).</summary>
public sealed class AuditSealingOptions
{
    public const string SectionName = "Audit:Sealing";

    /// <summary>A few seconds: an event waits at most this long for its place.</summary>
    public int Seconds { get; set; } = 5;
}

/// <summary>
/// The unit worker's loop (ADR-0050): seal until nothing waits, then send the anchors the control plane has not taken.
/// One worker per unit runs it; a second would wait on the chain's lock and find nothing.
/// </summary>
public sealed class AuditSealingWorker(IServiceScopeFactory scopes, AuditSealingOptions options, TimeProvider time, ILogger<AuditSealingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Seconds)), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sealer = scope.ServiceProvider.GetRequiredService<AuditSealer>();
                while (await sealer.SealAsync(stoppingToken).ConfigureAwait(false) is { } seal)
                {
                    logger.LogInformation("Sealed audit positions {First} to {Last}.", seal.FirstPosition, seal.LastPosition);
                }

                if (scope.ServiceProvider.GetService<AuditAnchorSender>() is { } sender)
                {
                    await sender.SendAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The audit sealing pass failed; it runs again in {Seconds} seconds.", options.Seconds);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
