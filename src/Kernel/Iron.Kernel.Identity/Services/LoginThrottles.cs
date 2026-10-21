using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>The tenant's lockout settings (TF-012).</summary>
public sealed record LockoutRules(int Threshold, TimeSpan Window, TimeSpan Duration);

/// <summary>
/// Lockout by login identifier (TF-012, ADR-0046). Every failed sign-in counts against the identifier typed, whether
/// anyone has it or not, so a locked answer reveals no account; while it is locked even the correct password is refused.
/// A wrong current password given to change it counts too, so an open session cannot be used to guess the password.
/// A success, an unlock or a recovery deletes the row; rows that hold neither a count nor a lock are deleted when a new
/// identifier is counted, so guessing at many identifiers leaves at most an hour of rows (the limits on spraying many
/// identifiers are T9's rate limits).
/// </summary>
public sealed class LoginThrottles(IdentityDbContext context, ISettings settings, TimeProvider time)
{
    private const int MaxAttempts = 3;

    /// <summary>The longest window a tenant may set: a row older than this counts nothing.</summary>
    private static readonly TimeSpan LongestWindow = TimeSpan.FromMinutes(60);

    public async Task<LockoutRules> RulesAsync(CancellationToken cancellationToken)
    {
        var threshold = await settings.GetAsync(IdentitySettings.LockoutThreshold, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var window = await settings.GetAsync(IdentitySettings.LockoutWindowMinutes, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var duration = await settings.GetAsync(IdentitySettings.LockoutDurationMinutes, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        return new LockoutRules(threshold, TimeSpan.FromMinutes(window), TimeSpan.FromMinutes(duration));
    }

    /// <summary>When the identifier's lock lifts, or null when it is not locked.</summary>
    public async Task<DateTimeOffset?> LockedUntilAsync(string loginId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(loginId))
        {
            return null;
        }

        var now = time.GetUtcNow();
        var until = await context.LoginThrottles.AsNoTracking()
            .Where(t => t.LoginId == loginId)
            .Select(t => t.LockedUntil)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return until is { } lifted && lifted > now ? lifted : null;
    }

    /// <summary>
    /// Counts one failure against the identifier and answers when it locked it, or null. Two failures at once for the
    /// same identifier race on its row (its version, or the unique identifier for a new one): the loser reads the row
    /// again and counts once more, so no failure is lost.
    /// </summary>
    public async Task<DateTimeOffset?> CountFailureAsync(string loginId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(loginId))
        {
            return null;
        }

        var rules = await RulesAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            var now = time.GetUtcNow();
            try
            {
                var throttle = await context.LoginThrottles.SingleOrDefaultAsync(t => t.LoginId == loginId, cancellationToken).ConfigureAwait(false);
                if (throttle is null)
                {
                    await ForgetStaleAsync(now, cancellationToken).ConfigureAwait(false);
                    throttle = new LoginThrottle(loginId);
                    context.LoginThrottles.Add(throttle);
                }

                var locked = throttle.RecordFailure(now, rules.Threshold, rules.Window, rules.Duration);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return locked ? throttle.LockedUntil : null;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                foreach (var entry in context.ChangeTracker.Entries<LoginThrottle>().ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    /// <summary>Forgets the identifier's failures and lifts its lock; answers whether it was locked.</summary>
    public async Task<bool> ClearAsync(string loginId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(loginId))
        {
            return false;
        }

        var now = time.GetUtcNow();
        var throttle = await context.LoginThrottles.SingleOrDefaultAsync(t => t.LoginId == loginId, cancellationToken).ConfigureAwait(false);
        if (throttle is null)
        {
            return false;
        }

        var wasLocked = throttle.IsLocked(now);
        context.LoginThrottles.Remove(throttle);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return wasLocked;
    }

    private async Task ForgetStaleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var before = now - LongestWindow;
        await context.LoginThrottles
            .Where(t => (t.LockedUntil == null || t.LockedUntil < now) && (t.WindowStartedAt == null || t.WindowStartedAt < before))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
