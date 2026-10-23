using System.Text.Json;
using Iron.Kernel.Data.Retention;
using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Sessions under their retention class (TF-066, ADR-0050): kept for the class's period after they ended, or after their
/// absolute limit when nobody ended them, then archived and deleted by the retention job. A session belongs to its
/// person, so a legal hold on the person keeps their sessions too.
/// </summary>
public sealed class SessionRetention(IdentityDbContext context) : IRetainedRecords
{
    public const string Type = "session";

    public string RecordType => Type;

    public RetentionClass Class => RetentionClasses.Session;

    public async Task<IReadOnlyList<RetainedRecord>> ExpiredAsync(DateTimeOffset cutoff, int max, CancellationToken cancellationToken)
    {
        var sessions = await context.Sessions.AsNoTracking()
            .Where(s => (s.EndedAt ?? s.ExpiresAt) < cutoff)
            .OrderBy(s => s.EndedAt ?? s.ExpiresAt)
            .Take(max)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. sessions.Select(s => new RetainedRecord(
            s.Id,
            s.EndedAt ?? s.ExpiresAt,
            UserService.RecordType,
            s.PrincipalId,
            JsonSerializer.Serialize(new
            {
                id = s.Id,
                principal_id = s.PrincipalId,
                started_at = s.CreatedAt,
                last_activity_at = s.LastActivityAt,
                ended_at = s.EndedAt,
                end_reason = s.EndReason,
                method = s.Method,
                browser = s.Browser,
                platform = s.Platform,
                city = s.City,
                country = s.Country,
                impersonator_id = s.ImpersonatorId,
            })))];
    }

    public async Task DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        await context.Sessions.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
}
