using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Iron.Core.Text;

namespace Iron.Core.Paging;

/// <summary>What a list field may be used for. A field can be returned, sorted on and filtered on, or only some of these.</summary>
[Flags]
public enum FieldUse
{
    Select = 1,
    Sort = 2,
    Filter = 4,
    All = Select | Sort | Filter,
}

/// <summary>
/// One field a list operation exposes: its API name (snake_case), the property it reads, and what callers may do with it.
/// The operators follow from the property type: text supports contains and startswith, numbers and dates support ranges.
/// </summary>
public sealed class ListField<T>
{
    internal ListField(string name, LambdaExpression selector, Type valueType, FieldUse use)
    {
        Name = name;
        Selector = selector;
        ValueType = valueType;
        Use = use;
        Operators = OperatorsFor(valueType);
    }

    public string Name { get; }

    /// <summary>The lambda <c>e => e.Property</c>, typed as <c>Expression&lt;Func&lt;T, TValue&gt;&gt;</c>.</summary>
    public LambdaExpression Selector { get; }

    public Type ValueType { get; }

    public FieldUse Use { get; }

    public IReadOnlyList<FilterOperator> Operators { get; }

    public bool Allows(FieldUse use) => (Use & use) == use;

    public bool Allows(FilterOperator filterOperator) => Operators.Contains(filterOperator);

    private static IReadOnlyList<FilterOperator> OperatorsFor(Type valueType)
    {
        var type = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (type == typeof(string))
        {
            return [FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.Contains, FilterOperator.StartsWith, FilterOperator.In];
        }

        if (type == typeof(bool))
        {
            return [FilterOperator.Equals, FilterOperator.NotEquals];
        }

        if (type == typeof(Guid) || type.IsEnum)
        {
            return [FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.In];
        }

        if (ListValues.IsOrdered(type))
        {
            return
            [
                FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.LessThan, FilterOperator.LessThanOrEqual,
                FilterOperator.GreaterThan, FilterOperator.GreaterThanOrEqual, FilterOperator.In,
            ];
        }

        return [FilterOperator.Equals, FilterOperator.NotEquals];
    }
}

/// <summary>
/// The fields one list operation exposes, by their API names (TF-101: the whitelist per resource). Declared once per
/// resource next to its endpoint; the API parser validates against it and the data layer applies it.
/// </summary>
public sealed class ListFields<T>
{
    private readonly Dictionary<string, ListField<T>> _byName = new(StringComparer.Ordinal);
    private readonly List<ListField<T>> _ordered = [];

    public IReadOnlyList<ListField<T>> All => _ordered;

    /// <summary>The order used when the caller gives none. Lists always have a stable order.</summary>
    public SortTerm? DefaultSort { get; private set; }

    public ListFields<T> Add<TValue>(string name, Expression<Func<T, TValue>> selector, FieldUse use = FieldUse.All)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (!Naming.IsSnakeCase(name))
        {
            throw new ArgumentException($"'{name}' is not a valid field name. Field names are snake_case, like created_at.", nameof(name));
        }

        if (_byName.ContainsKey(name))
        {
            throw new ArgumentException($"The field '{name}' is already defined.", nameof(name));
        }

        var field = new ListField<T>(name, selector, typeof(TValue), use);
        _byName[name] = field;
        _ordered.Add(field);
        return this;
    }

    public ListFields<T> SortByDefault(string name, SortDirection direction = SortDirection.Ascending)
    {
        var field = Get(name) ?? throw new ArgumentException($"'{name}' is not a field of this list.", nameof(name));
        if (!field.Allows(FieldUse.Sort))
        {
            throw new ArgumentException($"'{name}' is not sortable.", nameof(name));
        }

        DefaultSort = new SortTerm(name, direction);
        return this;
    }

    public ListField<T>? Get(string name) => _byName.GetValueOrDefault(name);

    public bool TryGet(string name, [NotNullWhen(true)] out ListField<T>? field) => _byName.TryGetValue(name, out field);

    public IEnumerable<string> Names(FieldUse use) => _ordered.Where(f => f.Allows(use)).Select(f => f.Name);
}
