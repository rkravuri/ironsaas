using System.Globalization;
using System.Text;
using Iron.Kernel.Data.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Kernel.Data.Encryption;

/// <summary>
/// A column of encrypted values or of their search hashes, as a module's model maps it; a hash column names the column of
/// the value it hashes.
/// </summary>
public sealed record EncryptedColumn(string Schema, string Table, string Column, KeyPurpose Purpose, string? SourceColumn = null)
{
    public string Name => $"{Schema}.{Table}.{Column}";
}

/// <summary>How far a re-encryption has come: values moved, and values to move when it started.</summary>
public sealed record ReencryptionProgress(long Done, long Total);

/// <summary>
/// The re-encryption job's work (TF-075 AC1, ADR-0054): after the data key is rotated, every encrypted value of every
/// module is re-wrapped to the new version inside the key service, a batch at a time; after the index key is rotated,
/// every search hash is made again from its value. A value already moved is never touched again, so an interrupted run
/// resumes where it stopped. The columns come from the modules' models: every <see cref="EncryptedValue"/> and every
/// <see cref="SearchHash"/> property. A row changed meanwhile keeps its own newer value: each update names the value it
/// replaces. The record's version and history are left alone, since its value did not change.
/// </summary>
public sealed class FieldReencryption(IServiceScopeFactory scopes, IEnumerable<RegisteredContext> contexts, NpgsqlDataSource dataSource, IKeyService keys, ILogger<FieldReencryption> logger)
{
    public const int DefaultBatchSize = 200;

    /// <summary>Every encrypted column and hash column of the registered modules.</summary>
    public IReadOnlyList<EncryptedColumn> Columns()
    {
        using var scope = scopes.CreateScope();
        var columns = new List<EncryptedColumn>();
        foreach (var registration in contexts)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
            columns.AddRange(ColumnsOf(context.Model));
        }

        return [.. columns.DistinctBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>The encrypted and hash columns of one model.</summary>
    public static IEnumerable<EncryptedColumn> ColumnsOf(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        foreach (var entityType in model.GetEntityTypes().Where(e => !e.IsOwned() && e.GetTableName() is not null))
        {
            var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(EncryptedValue))
                {
                    yield return new EncryptedColumn(table.Schema ?? "public", table.Name, property.GetColumnName(table)!, KeyPurpose.Data);
                }
                else if (property.ClrType == typeof(SearchHash) && property.Name.EndsWith("Hash", StringComparison.Ordinal)
                    && entityType.FindProperty(property.Name[..^4]) is { } source)
                {
                    yield return new EncryptedColumn(table.Schema ?? "public", table.Name, property.GetColumnName(table)!, KeyPurpose.Index, source.GetColumnName(table));
                }
            }
        }
    }

    /// <summary>How many values of the purpose are not yet on <paramref name="version"/> or later.</summary>
    public async Task<long> RemainingAsync(KeyPurpose purpose, int version, CancellationToken cancellationToken)
    {
        long remaining = 0;
        foreach (var column in Columns().Where(c => c.Purpose == purpose))
        {
            remaining += await CountAsync(column, version, cancellationToken).ConfigureAwait(false);
        }

        return remaining;
    }

    /// <summary>
    /// Moves every value of the purpose to <paramref name="version"/> or later, a batch at a time, and reports after each
    /// batch. Signing keys have no stored values, so their run has nothing to do.
    /// </summary>
    public async Task<ReencryptionProgress> RunAsync(KeyPurpose purpose, int version, Func<ReencryptionProgress, CancellationToken, Task>? report, CancellationToken cancellationToken, int batchSize = DefaultBatchSize)
    {
        var columns = Columns().Where(c => c.Purpose == purpose).ToList();
        long total = 0;
        foreach (var column in columns)
        {
            total += await CountAsync(column, version, cancellationToken).ConfigureAwait(false);
        }

        long done = 0;
        foreach (var column in columns)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var moved = purpose == KeyPurpose.Index
                    ? await RehashBatchAsync(column, version, batchSize, cancellationToken).ConfigureAwait(false)
                    : await RewrapBatchAsync(column, version, batchSize, cancellationToken).ConfigureAwait(false);
                if (moved == 0)
                {
                    break;
                }

                done += moved;
                if (report is not null)
                {
                    await report(new ReencryptionProgress(Math.Min(done, Math.Max(total, done)), Math.Max(total, done)), cancellationToken).ConfigureAwait(false);
                }
            }

            logger.LogInformation("Moved the values of {Column} to version {Version} of the {Purpose} key.", column.Name, version, purpose.Word());
        }

        return new ReencryptionProgress(done, Math.Max(total, done));
    }

    private async Task<int> RewrapBatchAsync(EncryptedColumn column, int version, int batchSize, CancellationToken cancellationToken)
    {
        var rows = await SelectAsync(column, version, batchSize, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return 0;
        }

        var moved = await keys.RewrapAsync(KeyPurpose.Data, [.. rows.Select(r => r.Value)], cancellationToken).ConfigureAwait(false);
        return await UpdateAsync(column, [.. rows.Select((row, i) => (row.Id, row.Value, moved[i]))], cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RehashBatchAsync(EncryptedColumn column, int version, int batchSize, CancellationToken cancellationToken)
    {
        var rows = await SelectAsync(column, version, batchSize, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return 0;
        }

        // The hash is of the normalised value, so the value is opened, normalised and hashed again, a batch at a time.
        var values = await keys.DecryptAsync(KeyPurpose.Data, [.. rows.Select(r => r.Source!)], cancellationToken).ConfigureAwait(false);
        var inputs = values.Select(v => Encoding.UTF8.GetBytes(SearchNormalisation.Normalise(Encoding.UTF8.GetString(v)))).ToList();
        var hashes = await keys.HashAsync(KeyPurpose.Index, inputs, version, cancellationToken).ConfigureAwait(false);
        return await UpdateAsync(column, [.. rows.Select((row, i) => (row.Id, row.Value, hashes[i]))], cancellationToken).ConfigureAwait(false);
    }

    [SqlFromIdentifiers("Counts a module's encrypted column by the schema, table and column names its model maps, quoted; PostgreSQL takes no parameters for identifiers.")]
    private async Task<long> CountAsync(EncryptedColumn column, int version, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"SELECT count(*) FROM {Table(column)} WHERE {Quote(column.Column)} IS NOT NULL AND {OlderThan(column.Column)}");
        command.Parameters.AddWithValue("version", version);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    [SqlFromIdentifiers("Reads a batch of a module's encrypted column by the schema, table and column names its model maps, quoted; PostgreSQL takes no parameters for identifiers.")]
    private async Task<IReadOnlyList<(Guid Id, string Value, string? Source)>> SelectAsync(EncryptedColumn column, int version, int batchSize, CancellationToken cancellationToken)
    {
        var source = column.SourceColumn is null ? string.Empty : $", {Quote(column.SourceColumn)}";
        var sourceFilter = column.SourceColumn is null ? string.Empty : $" AND {Quote(column.SourceColumn)} IS NOT NULL";
        await using var command = dataSource.CreateCommand(
            $"SELECT id, {Quote(column.Column)}{source} FROM {Table(column)} WHERE {Quote(column.Column)} IS NOT NULL{sourceFilter} AND {OlderThan(column.Column)} ORDER BY id LIMIT @limit");
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("limit", batchSize);
        var rows = new List<(Guid, string, string?)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1), column.SourceColumn is null ? null : reader.GetString(2)));
        }

        return rows;
    }

    [SqlFromIdentifiers("Writes a batch of a module's encrypted column by the schema, table and column names its model maps, quoted; PostgreSQL takes no parameters for identifiers.")]
    private async Task<int> UpdateAsync(EncryptedColumn column, IReadOnlyList<(Guid Id, string Old, string New)> rows, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(connection);
        var sql = $"UPDATE {Table(column)} SET {Quote(column.Column)} = $1 WHERE id = $2 AND {Quote(column.Column)} = $3";
        foreach (var (id, old, replacement) in rows)
        {
            var command = new NpgsqlBatchCommand(sql);
            command.Parameters.Add(new NpgsqlParameter { Value = replacement });
            command.Parameters.Add(new NpgsqlParameter { Value = id });
            command.Parameters.Add(new NpgsqlParameter { Value = old });
            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // A row changed meanwhile keeps its own value, which is already on the new version: either way it has moved.
        return rows.Count;
    }

    private static string OlderThan(string column) => $"CAST(substring(split_part({Quote(column)}, ':', 2) FROM 2) AS integer) < @version";

    private static string Table(EncryptedColumn column) => $"{Quote(column.Schema)}.{Quote(column.Table)}";

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
