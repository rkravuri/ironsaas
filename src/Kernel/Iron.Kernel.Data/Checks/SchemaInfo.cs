namespace Iron.Kernel.Data.Checks;

/// <summary>One column of a table as the database reports it.</summary>
public sealed record ColumnInfo(string Name, string DataType, bool IsNullable);

/// <summary>One table as the database reports it, with its comment, columns and primary key columns.</summary>
public sealed record TableInfo(
    string Schema,
    string Name,
    string? Comment,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<string> PrimaryKeyColumns)
{
    public string QualifiedName => $"{Schema}.{Name}";

    public ColumnInfo? Column(string name) => Columns.FirstOrDefault(c => c.Name == name);
}

/// <summary>One foreign key constraint and the table it points to.</summary>
public sealed record ForeignKeyInfo(string Name, string Schema, string Table, string ReferencedSchema, string ReferencedTable);
