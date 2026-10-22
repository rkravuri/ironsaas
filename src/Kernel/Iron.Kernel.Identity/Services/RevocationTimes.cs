using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// The tenant's "valid after" (TF-029, ADR-0049): the time of its latest revocation, before which no session started and
/// no token issued counts. Every request asks, so the answer is kept for at most <see cref="CacheFor"/>: an API process
/// that did not take the revocation itself follows within a minute, and the one that took it at once. A principal's own
/// "valid after" needs no cache: the principal's row is read on every request anyway.
/// </summary>
public sealed class RevocationTimes(TimeProvider time)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private (DateTimeOffset? Value, DateTimeOffset ReadAt)? _cached;

    public async Task<DateTimeOffset?> TenantValidAfterAsync(IdentityDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_cached is { } cached && now - cached.ReadAt < CacheFor)
            {
                return cached.Value;
            }
        }

        var value = await context.TenantRevocations.AsNoTracking().MaxAsync(r => (DateTimeOffset?)r.RevokedAt, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _cached = (value, now);
        }

        return value;
    }

    /// <summary>This process took a revocation: it holds from the next request, not after the cache runs out.</summary>
    public void Revoked(DateTimeOffset at)
    {
        lock (_gate)
        {
            _cached = (at, time.GetUtcNow());
        }
    }

    /// <summary>
    /// Whether a token issued at <paramref name="issuedAt"/> came before <paramref name="validAfter"/>. A token's issue
    /// time has whole seconds, so a token issued in the second of a revocation, before or after it, is refused too: the
    /// safe side, and its client asks again a second later.
    /// </summary>
    public static bool IssuedBefore(DateTimeOffset? issuedAt, DateTimeOffset? validAfter)
    {
        if (validAfter is not { } after)
        {
            return false;
        }

        if (issuedAt is not { } issued)
        {
            return true;
        }

        var firstWholeSecond = after.UtcTicks % TimeSpan.TicksPerSecond == 0 ? after : new DateTimeOffset(after.UtcTicks - (after.UtcTicks % TimeSpan.TicksPerSecond) + TimeSpan.TicksPerSecond, TimeSpan.Zero);
        return issued < firstWholeSecond;
    }
}
