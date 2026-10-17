using System.Linq.Expressions;
using System.Reflection;
using Iron.Core.Paging;

namespace Iron.Data.Lists;

/// <summary>Builds the predicate for one filter term. Text comparisons ignore case; <c>in</c> takes comma separated values.</summary>
public static class FilterExpressions
{
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!;
    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), new[] { typeof(string) })!;
    private static readonly MethodInfo StringToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    public static Expression<Func<T, bool>> Build<T>(ListField<T> field, FilterTerm term)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(term);

        var parameter = field.Selector.Parameters[0];
        var member = field.Selector.Body;

        Expression body = term.Operator switch
        {
            FilterOperator.Equals => Expression.Equal(member, Constant(field, term.Value)),
            FilterOperator.NotEquals => Expression.NotEqual(member, Constant(field, term.Value)),
            FilterOperator.LessThan => Expression.LessThan(member, Constant(field, term.Value)),
            FilterOperator.LessThanOrEqual => Expression.LessThanOrEqual(member, Constant(field, term.Value)),
            FilterOperator.GreaterThan => Expression.GreaterThan(member, Constant(field, term.Value)),
            FilterOperator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(member, Constant(field, term.Value)),
            FilterOperator.Contains => TextMatch(member, StringContains, term.Value),
            FilterOperator.StartsWith => TextMatch(member, StringStartsWith, term.Value),
            FilterOperator.In => InList(field, member, term.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(term), term.Operator, "Unknown filter operator."),
        };

        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static ConstantExpression Constant<T>(ListField<T> field, string text)
    {
        return Expression.Constant(Parse(field, text), field.ValueType);
    }

    private static object? Parse<T>(ListField<T> field, string text)
    {
        if (!ListValues.TryParse(field.ValueType, text, out var value))
        {
            throw new ArgumentException($"'{text}' is not a valid value for '{field.Name}'.");
        }

        return value;
    }

    // e.Name != null && e.Name.ToLower().Contains("value"); translates to LOWER(...) LIKE in SQL and works in memory.
    private static Expression TextMatch(Expression member, MethodInfo method, string value)
    {
        var notNull = Expression.NotEqual(member, Expression.Constant(null, typeof(string)));
        var lowered = Expression.Call(member, StringToLower);
        var match = Expression.Call(lowered, method, Expression.Constant(value.ToLowerInvariant()));
        return Expression.AndAlso(notNull, match);
    }

    // values.Contains(e.Field); translates to IN (...).
    private static Expression InList<T>(ListField<T> field, Expression member, string text)
    {
        var listType = typeof(List<>).MakeGenericType(field.ValueType);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            list.Add(Parse(field, part));
        }

        return Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), new[] { field.ValueType }, Expression.Constant(list, listType), member);
    }
}
