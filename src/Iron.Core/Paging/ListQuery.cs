namespace Iron.Core.Paging;

public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>One sort term, for example ("created_at", Descending).</summary>
public sealed record SortTerm(string Field, SortDirection Direction);

/// <summary>Filter operators of the list grammar (TF-101). Which operators a field accepts depends on its type.</summary>
public enum FilterOperator
{
    Equals,
    NotEquals,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Contains,
    StartsWith,
    In,
}

/// <summary>One filter term, for example ("status", Equals, "Active"). The value is the raw text; the data layer converts it by field type.</summary>
public sealed record FilterTerm(string Field, FilterOperator Operator, string Value);

/// <summary>
/// Everything a list operation accepts: a page, sort terms, filter terms and the fields to return (TF-101).
/// The API layer parses it from the query string (T5); the data layer applies it with a whitelist per resource (T4, T5).
/// </summary>
public sealed record ListQuery(
    PageRequest Page,
    IReadOnlyList<SortTerm> Sorts,
    IReadOnlyList<FilterTerm> Filters,
    IReadOnlyList<string> Fields)
{
    public static ListQuery Default => new(PageRequest.Default, [], [], []);
}
