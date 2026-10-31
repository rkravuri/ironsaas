using System.Globalization;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// <c>identity.token_cleanup</c> (TF-124, ADR-0061): every hour, the one-time proofs nobody needs any more go: the sign-in
/// challenges past their time, the sign-in links expired more than <see cref="LinksKeptDays"/> days ago, used or not, and
/// the counters of failed sign-ins idle for a day and not locking anyone. The audit trail and a person's security activity
/// keep what happened; sessions follow their retention class (<c>audit.retention</c>). A repeated run removes nothing more.
/// </summary>
public sealed class TokenCleanupJob(IdentityDbContext context, TimeProvider time) : IJob
{
    /// <summary>How long an expired sign-in link stays, so an investigation can still match one to the trail.</summary>
    public const int LinksKeptDays = 30;

    public static readonly JobDefinition Definition = JobDefinition.Create<TokenCleanupJob>(
        "identity.token_cleanup", "Removes expired sign-in challenges and links, and idle counters of failed sign-ins.", "Platform security",
        JobSchedule.Interval(TimeSpan.FromHours(1), firstAfter: TimeSpan.FromMinutes(5)), PriorityClass.Bulk, TimeSpan.FromMinutes(10));

    public async Task<Result> RunAsync(JobContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var now = time.GetUtcNow();
        var challenges = await context.AuthenticationChallenges.Where(c => c.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var linksBefore = now.AddDays(-LinksKeptDays);
        var links = await context.SignInLinks.Where(l => l.ExpiresAt < linksBefore).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var idleBefore = now.AddDays(-1);
        var throttles = await context.LoginThrottles
            .Where(t => (t.LockedUntil == null || t.LockedUntil < now) && (t.WindowStartedAt == null || t.WindowStartedAt < idleBefore))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        run.Log(string.Create(CultureInfo.InvariantCulture, $"Removed {challenges} challenge(s), {links} sign-in link(s) and {throttles} idle counter(s)."));
        return Result.Success();
    }
}
