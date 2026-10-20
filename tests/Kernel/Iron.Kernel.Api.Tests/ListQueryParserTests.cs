using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Tests.Sample;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Iron.Kernel.Api.Tests;

public class ListQueryParserTests
{
    [Fact]
    [Proves("ADR-0009")]
    public void ParsesEveryPartOfTheGrammar()
    {
        var query = Query(("page_number", "2"), ("page_size", "25"), ("sort", "-weight,name"), ("filter", "status:eq:active"), ("filter", "weight:in:10,20"), ("fields", "id,name"));

        var result = ListQueryParser.Parse(query, Things.Fields);

        Assert.True(result.IsSuccess);
        var parsed = result.Value;
        Assert.Equal(2, parsed.Page.PageNumber);
        Assert.Equal(25, parsed.Page.PageSize);
        SortTerm[] sorts = [new("weight", SortDirection.Descending), new("name", SortDirection.Ascending)];
        FilterTerm[] filters = [new("status", FilterOperator.Equals, "active"), new("weight", FilterOperator.In, "10,20")];
        string[] fields = ["id", "name"];
        Assert.Equal(sorts, parsed.Sorts);
        Assert.Equal(filters, parsed.Filters);
        Assert.Equal(fields, parsed.Fields);
    }

    [Fact]
    [Untraced("The parser's answer to an empty query string, the default page and no terms, is stated in no criterion or ADR.")]
    public void AnEmptyQueryIsTheDefaultListQuery()
    {
        var result = ListQueryParser.Parse(Query(), Things.Fields);

        Assert.True(result.IsSuccess);
        Assert.Equal(PageRequest.Default, result.Value.Page);
        Assert.Empty(result.Value.Sorts);
        Assert.Empty(result.Value.Filters);
        Assert.Empty(result.Value.Fields);
    }

    [Theory]
    [Proves("ADR-0009")]
    [InlineData("page_number", "0", "invalid_number")]
    [InlineData("page_size", "many", "invalid_number")]
    [InlineData("sort", "colour", "unknown_field")]
    [InlineData("sort", "-created_at,colour", "unknown_field")]
    [InlineData("filter", "weight:gte", "invalid_filter")]
    [InlineData("filter", "colour:eq:red", "unknown_field")]
    [InlineData("filter", "weight:like:1", "invalid_operator")]
    [InlineData("filter", "name:gt:a", "operator_not_allowed")]
    [InlineData("filter", "weight:eq:heavy", "invalid_value")]
    [InlineData("filter", "status:eq:unknown", "invalid_value")]
    [InlineData("filter", "created_at:eq:2026-01-01", "unknown_field")]
    [InlineData("fields", "id,colour", "unknown_field")]
    public void ReportsEachProblemOnItsQueryParameter(string key, string value, string expectedCode)
    {
        var result = ListQueryParser.Parse(Query((key, value)), Things.Fields);

        Assert.True(result.IsFailure);
        Assert.Equal("api.list.invalid", result.Error.Code);
        var field = Assert.Single(result.Error.Fields);
        Assert.Equal(key, field.Field);
        Assert.Equal(expectedCode, field.Code);
    }

    [Fact]
    [Untraced("Reporting every list query problem in one answer is stated in no criterion or ADR.")]
    public void CollectsEveryProblemAtOnce()
    {
        var result = ListQueryParser.Parse(Query(("sort", "colour"), ("filter", "colour:eq:red"), ("fields", "colour")), Things.Fields);

        Assert.True(result.IsFailure);
        Assert.Equal(3, result.Error.Fields.Count);
    }

    private static QueryCollection Query(params (string Key, string Value)[] pairs)
    {
        var values = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            values[key] = values.TryGetValue(key, out var existing) ? StringValues.Concat(existing, value) : new StringValues(value);
        }

        return new QueryCollection(values);
    }
}
