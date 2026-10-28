namespace Iron.Kernel;

/// <summary>
/// The method builds SQL text from identifiers, such as the name of a role or a schema, which PostgreSQL takes no
/// parameters for (TF-071 AC3, ADR-0053). The SQL analyzer (IRON0010) lets such text pass in this method alone, and asks
/// for the reason (IRON0011): which identifiers, and how they are checked and quoted. Values never go this way; they are
/// parameters.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
public sealed class SqlFromIdentifiersAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
