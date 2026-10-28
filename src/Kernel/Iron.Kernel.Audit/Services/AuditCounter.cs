using System.Data;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Iron.Kernel.Audit.Services;

/// <summary>
/// Counts audit events by group for the alert rules (ADR-0056), one query per kind of event, added up by group. The
/// grouping and the detail a rule reads are values of the query, not text of it: a detail is read with <c>-&gt;&gt;</c>
/// and a parameter, so a rule's key never becomes SQL. The window's bounds and the event's type find the rows by the
/// indexes on <c>created_at</c> and <c>event_type</c>.
/// </summary>
public sealed class AuditCounter(AuditDbContext context) : IAuditCounts
{
    /// <summary>The group of <see cref="AuditGrouping.Everything"/>.</summary>
    public const string All = "all";

    // A whole number in a detail is at most 18 digits, so its cast to bigint cannot overflow; anything else is no number.
    private const string CountSql = """
        select grp, count(*)::int as events, coalesce(sum(measure), 0)::bigint as measure, min(created_at) as first, max(created_at) as last
        from (
            select case @grouping
                       when 0 then nullif(actor_id, '00000000-0000-0000-0000-000000000000'::uuid)::text
                       when 1 then nullif(details ->> 'address', '')
                       when 2 then nullif(target, '')
                       when 3 then record_id::text
                       else 'all'
                   end as grp,
                   case
                       when @sum_key is null then 1
                       when details ->> @sum_key ~ '^[0-9]+$' and length(details ->> @sum_key) < 19 then (details ->> @sum_key)::bigint
                       else 0
                   end as measure,
                   created_at
            from kernel_audit.events
            where event_type = @event_type
              and created_at > @after
              and created_at <= @until
              and (@outcome is null or outcome = @outcome)
              and (@detail_key is null or details ->> @detail_key = @detail_value)
              and (@target_prefix is null or starts_with(target, @target_prefix))
              and (@quantity_key is null
                   or case when details ->> @quantity_key ~ '^[0-9]+$' and length(details ->> @quantity_key) < 19
                           then (details ->> @quantity_key)::bigint > @above
                           else false
                      end)
        ) counted
        where grp is not null and (@group is null or grp = @group)
        group by grp
        """;

    public async Task<IReadOnlyList<AuditGroupCount>> CountAsync(AuditCountQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var groups = new Dictionary<string, AuditGroupCount>(StringComparer.Ordinal);
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            foreach (var match in query.Matches)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = CountSql;
                command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
                command.Parameters.Add(new NpgsqlParameter("grouping", NpgsqlDbType.Integer) { Value = (int)query.GroupBy });
                command.Parameters.Add(Text("sum_key", query.SumKey));
                command.Parameters.Add(Text("event_type", match.EventType));
                command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.TimestampTz) { Value = query.After.UtcDateTime });
                command.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz) { Value = query.Until.UtcDateTime });
                command.Parameters.Add(Text("outcome", match.Outcome));
                command.Parameters.Add(Text("detail_key", match.DetailKey));
                command.Parameters.Add(Text("detail_value", match.DetailValue));
                command.Parameters.Add(Text("target_prefix", match.TargetPrefix));
                command.Parameters.Add(Text("quantity_key", query.QuantityKey));
                command.Parameters.Add(new NpgsqlParameter("above", NpgsqlDbType.Bigint) { Value = query.Above ?? 0 });
                command.Parameters.Add(Text("group", query.Group));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var found = new AuditGroupCount(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        reader.GetInt64(2),
                        new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
                        new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero));
                    groups[found.Group] = groups.TryGetValue(found.Group, out var before)
                        ? new AuditGroupCount(found.Group, before.Events + found.Events, before.Measure + found.Measure, Min(before.First, found.First), Max(before.Last, found.Last))
                        : found;
                }
            }
        }
        finally
        {
            if (opened)
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        return [.. groups.Values.OrderBy(g => g.Group, StringComparer.Ordinal)];
    }

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value };

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
}
