using System.Globalization;
using Iron.Api.Problems;
using Iron.Kernel;
using Microsoft.AspNetCore.Http;

namespace Iron.Api.Lists;

/// <summary>
/// The list grammar of the query string (TF-101), checked against the resource's <see cref="ListFields{T}"/>:
/// <c>page_number=2&amp;page_size=50</c>, <c>sort=-created_at,name</c> (a leading minus means descending),
/// <c>filter=status:eq:active</c> (repeatable; <c>in</c> takes comma separated values), <c>fields=id,name</c>.
/// Every problem names the query parameter and the field, so the caller can fix it.
/// </summary>
public static class ListQueryParser
{
    public const string PageNumberKey = "page_number";
    public const string PageSizeKey = "page_size";
    public const string SortKey = "sort";
    public const string FilterKey = "filter";
    public const string FieldsKey = "fields";

    private static readonly Dictionary<string, FilterOperator> Operators = new(StringComparer.Ordinal)
    {
        ["eq"] = FilterOperator.Equals,
        ["ne"] = FilterOperator.NotEquals,
        ["lt"] = FilterOperator.LessThan,
        ["lte"] = FilterOperator.LessThanOrEqual,
        ["gt"] = FilterOperator.GreaterThan,
        ["gte"] = FilterOperator.GreaterThanOrEqual,
        ["contains"] = FilterOperator.Contains,
        ["startswith"] = FilterOperator.StartsWith,
        ["in"] = FilterOperator.In,
    };

    public static string OperatorName(FilterOperator filterOperator) => Operators.First(pair => pair.Value == filterOperator).Key;

    public static Result<ListQuery> Parse<T>(IQueryCollection query, ListFields<T> fields)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(fields);

        var errors = new List<FieldError>();
        var pageNumber = ParsePageValue(query, PageNumberKey, errors);
        var pageSize = ParsePageValue(query, PageSizeKey, errors);
        var sorts = ParseSorts(query, fields, errors);
        var filters = ParseFilters(query, fields, errors);
        var selected = ParseFields(query, fields, errors);

        if (errors.Count > 0)
        {
            return Error.Validation(ApiErrors.ListInvalid, "The list query is not valid.", errors);
        }

        return new ListQuery(PageRequest.From(pageNumber, pageSize), sorts, filters, selected);
    }

    private static int? ParsePageValue(IQueryCollection query, string key, List<FieldError> errors)
    {
        var raw = Values(query, key).LastOrDefault();
        if (raw is null)
        {
            return null;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 1)
        {
            return number;
        }

        errors.Add(new FieldError(key, "invalid_number", $"'{raw}' is not valid for {key}. Use a whole number from 1."));
        return null;
    }

    private static List<SortTerm> ParseSorts<T>(IQueryCollection query, ListFields<T> fields, List<FieldError> errors)
    {
        var sorts = new List<SortTerm>();
        foreach (var part in Values(query, SortKey).SelectMany(value => Split(value)))
        {
            var descending = part.StartsWith('-');
            var name = descending ? part[1..] : part;
            if (!fields.TryGet(name, out var field) || !field.Allows(FieldUse.Sort))
            {
                errors.Add(new FieldError(SortKey, "unknown_field", $"'{name}' is not a sortable field. Sortable fields: {Join(fields.Names(FieldUse.Sort))}."));
                continue;
            }

            sorts.Add(new SortTerm(name, descending ? SortDirection.Descending : SortDirection.Ascending));
        }

        return sorts;
    }

    private static List<FilterTerm> ParseFilters<T>(IQueryCollection query, ListFields<T> fields, List<FieldError> errors)
    {
        var filters = new List<FilterTerm>();
        foreach (var raw in Values(query, FilterKey))
        {
            var parts = raw.Split(':', 3);
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                errors.Add(new FieldError(FilterKey, "invalid_filter", $"'{raw}' is not a valid filter. Use field:operator:value, for example status:eq:active."));
                continue;
            }

            var name = parts[0];
            if (!fields.TryGet(name, out var field) || !field.Allows(FieldUse.Filter))
            {
                errors.Add(new FieldError(FilterKey, "unknown_field", $"'{name}' is not a filterable field. Filterable fields: {Join(fields.Names(FieldUse.Filter))}."));
                continue;
            }

            if (!Operators.TryGetValue(parts[1], out var filterOperator))
            {
                errors.Add(new FieldError(FilterKey, "invalid_operator", $"'{parts[1]}' is not an operator. Use one of: {Join(Operators.Keys)}."));
                continue;
            }

            if (!field.Allows(filterOperator))
            {
                errors.Add(new FieldError(FilterKey, "operator_not_allowed", $"'{parts[1]}' cannot be used on '{name}'. Allowed: {Join(field.Operators.Select(o => OperatorName(o)))}."));
                continue;
            }

            var value = parts[2];
            List<string> samples = filterOperator == FilterOperator.In ? Split(value) : [value];
            var badSample = samples.FirstOrDefault(sample => !ListValues.TryParse(field.ValueType, sample, out _));
            if (badSample is not null || samples.Count == 0)
            {
                errors.Add(new FieldError(FilterKey, "invalid_value", $"'{badSample ?? value}' is not a valid value for '{name}'."));
                continue;
            }

            filters.Add(new FilterTerm(name, filterOperator, value));
        }

        return filters;
    }

    private static List<string> ParseFields<T>(IQueryCollection query, ListFields<T> fields, List<FieldError> errors)
    {
        var selected = new List<string>();
        foreach (var name in Values(query, FieldsKey).SelectMany(value => Split(value)))
        {
            if (!fields.TryGet(name, out var field) || !field.Allows(FieldUse.Select))
            {
                errors.Add(new FieldError(FieldsKey, "unknown_field", $"'{name}' is not a field of this resource. Fields: {Join(fields.Names(FieldUse.Select))}."));
                continue;
            }

            if (!selected.Contains(name, StringComparer.Ordinal))
            {
                selected.Add(name);
            }
        }

        return selected;
    }

    private static IEnumerable<string> Values(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values))
        {
            return [];
        }

        return values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim());
    }

    private static List<string> Split(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Join(IEnumerable<string> names) => string.Join(", ", names);
}
