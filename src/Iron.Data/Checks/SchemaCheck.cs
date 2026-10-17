using Iron.Data.Conventions;
using Npgsql;

namespace Iron.Data.Checks;

/// <summary>
/// Checks a live database against the table conventions of TF-080: snake_case names, a <c>uuid</c> primary key
/// named <c>id</c>, the created, updated and version columns (only created for append-only tables), soft-delete
/// columns that belong together, and no foreign key from one schema into another. Runs in the integration tests
/// and, from T7, against every migrated database in the pipeline.
/// </summary>
public static class SchemaCheck
{
    public const string Timestamp = "timestamp with time zone";
    public const string Uuid = "uuid";
    public const string Integer = "integer";
    public const string Boolean = "boolean";

    public static async Task<IReadOnlyList<string>> RunAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyCollection<string> schemas,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(connection, schemas, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<string>> RunAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> schemas,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schemas);

        var tables = await LoadTablesAsync(connection, schemas, cancellationToken).ConfigureAwait(false);
        var foreignKeys = await LoadForeignKeysAsync(connection, schemas, cancellationToken).ConfigureAwait(false);
        return Evaluate(tables, foreignKeys);
    }

    /// <summary>The pure rules, so they can be unit tested without a database.</summary>
    public static IReadOnlyList<string> Evaluate(IReadOnlyList<TableInfo> tables, IReadOnlyList<ForeignKeyInfo> foreignKeys)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(foreignKeys);

        var violations = new List<string>();

        foreach (var table in tables)
        {
            if (table.Name.StartsWith("__", StringComparison.Ordinal))
            {
                continue; // the EF Core migrations history table
            }

            if (!IsSnakeCase(table.Name))
            {
                violations.Add($"{table.QualifiedName}: table names are snake_case.");
            }

            foreach (var column in table.Columns)
            {
                if (!IsSnakeCase(column.Name))
                {
                    violations.Add($"{table.QualifiedName}.{column.Name}: column names are snake_case.");
                }
            }

            if (table.PrimaryKeyColumns.Count != 1 || table.PrimaryKeyColumns[0] != "id")
            {
                violations.Add($"{table.QualifiedName}: the primary key must be the single column 'id'.");
            }

            RequireColumn(violations, table, "id", Uuid);
            RequireColumn(violations, table, "created_at", Timestamp);
            RequireColumn(violations, table, "created_by", Uuid);

            var appendOnly = string.Equals(table.Comment, ModelConventions.AppendOnlyComment, StringComparison.Ordinal);
            if (!appendOnly)
            {
                RequireColumn(violations, table, "updated_at", Timestamp);
                RequireColumn(violations, table, "updated_by", Uuid);
                RequireColumn(violations, table, "version", Integer);
            }

            if (table.Column("is_deleted") is not null)
            {
                RequireColumn(violations, table, "is_deleted", Boolean);
                RequireColumn(violations, table, "deleted_at", Timestamp, nullable: true);
                RequireColumn(violations, table, "deleted_by", Uuid, nullable: true);
            }
        }

        foreach (var foreignKey in foreignKeys)
        {
            if (!string.Equals(foreignKey.Schema, foreignKey.ReferencedSchema, StringComparison.Ordinal))
            {
                violations.Add(
                    $"{foreignKey.Schema}.{foreignKey.Table}: foreign key {foreignKey.Name} points into schema {foreignKey.ReferencedSchema}. Modules reference each other by identifier, never by foreign key (TF-080).");
            }
        }

        return violations;
    }

    public static bool IsSnakeCase(string? name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsAsciiLetterLower(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireColumn(List<string> violations, TableInfo table, string name, string dataType, bool nullable = false)
    {
        var column = table.Column(name);
        if (column is null)
        {
            violations.Add($"{table.QualifiedName}: missing column '{name}' ({dataType}).");
            return;
        }

        if (!string.Equals(column.DataType, dataType, StringComparison.Ordinal))
        {
            violations.Add($"{table.QualifiedName}.{name}: expected type {dataType}, found {column.DataType}.");
        }

        if (!nullable && column.IsNullable)
        {
            violations.Add($"{table.QualifiedName}.{name}: must be NOT NULL.");
        }
    }

    private static async Task<List<TableInfo>> LoadTablesAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> schemas,
        CancellationToken cancellationToken)
    {
        var schemaList = schemas.ToArray();
        var columns = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);
        var primaryKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var tables = new List<TableInfo>();

        const string columnSql = """
            select table_schema::text, table_name::text, column_name::text, data_type::text, is_nullable::text
            from information_schema.columns
            where table_schema::text = any(@schemas)
            order by table_schema, table_name, ordinal_position
            """;
        await using (var command = new NpgsqlCommand(columnSql, connection))
        {
            command.Parameters.AddWithValue("schemas", schemaList);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = reader.GetString(0) + "." + reader.GetString(1);
                if (!columns.TryGetValue(key, out var list))
                {
                    list = [];
                    columns[key] = list;
                }

                list.Add(new ColumnInfo(reader.GetString(2), reader.GetString(3), reader.GetString(4) == "YES"));
            }
        }

        const string primaryKeySql = """
            select n.nspname::text, c.relname::text, a.attname::text
            from pg_constraint k
            join pg_class c on c.oid = k.conrelid
            join pg_namespace n on n.oid = c.relnamespace
            join unnest(k.conkey) with ordinality as cols(attnum, ord) on true
            join pg_attribute a on a.attrelid = c.oid and a.attnum = cols.attnum
            where k.contype = 'p' and n.nspname::text = any(@schemas)
            order by 1, 2, cols.ord
            """;
        await using (var command = new NpgsqlCommand(primaryKeySql, connection))
        {
            command.Parameters.AddWithValue("schemas", schemaList);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = reader.GetString(0) + "." + reader.GetString(1);
                if (!primaryKeys.TryGetValue(key, out var list))
                {
                    list = [];
                    primaryKeys[key] = list;
                }

                list.Add(reader.GetString(2));
            }
        }

        const string tableSql = """
            select n.nspname::text, c.relname::text, obj_description(c.oid, 'pg_class')
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p') and n.nspname::text = any(@schemas)
            order by 1, 2
            """;
        await using (var command = new NpgsqlCommand(tableSql, connection))
        {
            command.Parameters.AddWithValue("schemas", schemaList);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                var comment = reader.IsDBNull(2) ? null : reader.GetString(2);
                var key = schema + "." + name;
                tables.Add(new TableInfo(
                    schema,
                    name,
                    comment,
                    columns.TryGetValue(key, out var tableColumns) ? tableColumns : [],
                    primaryKeys.TryGetValue(key, out var keyColumns) ? keyColumns : []));
            }
        }

        return tables;
    }

    private static async Task<List<ForeignKeyInfo>> LoadForeignKeysAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> schemas,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select k.conname::text, n.nspname::text, c.relname::text, rn.nspname::text, rc.relname::text
            from pg_constraint k
            join pg_class c on c.oid = k.conrelid
            join pg_namespace n on n.oid = c.relnamespace
            join pg_class rc on rc.oid = k.confrelid
            join pg_namespace rn on rn.oid = rc.relnamespace
            where k.contype = 'f' and n.nspname::text = any(@schemas)
            order by 2, 3, 1
            """;

        var result = new List<ForeignKeyInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schemas", schemas.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ForeignKeyInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return result;
    }
}
