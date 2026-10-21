namespace Iron.Kernel.Tests;

public class PagingTests
{
    [Fact]
    [Proves("ADR-0009")]
    public void TheDefaultRequestIsTheFirstPageOfFifty()
    {
        var request = PageRequest.Default;

        Assert.Equal(1, request.PageNumber);
        Assert.Equal(50, request.PageSize);
        Assert.Equal(0, request.Skip);
        Assert.False(request.WasCapped);
    }

    [Fact]
    [Proves("TF-101 AC2")]
    public void APageSizeAboveTheMaximumIsCappedAndReported()
    {
        var request = new PageRequest(3, 500);

        Assert.Equal(200, request.PageSize);
        Assert.True(request.WasCapped);
        Assert.Equal(400, request.Skip);
    }

    [Theory]
    [Proves("ADR-0009")]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(7, 7)]
    public void PageNumbersBelowOneBecomeOne(int given, int expected)
    {
        Assert.Equal(expected, new PageRequest(given, 10).PageNumber);
    }

    [Fact]
    [Proves("ADR-0009")]
    public void MissingQueryValuesTakeTheDefaults()
    {
        var request = PageRequest.From(null, null);

        Assert.Equal(PageRequest.Default, request);
        Assert.Equal(25, PageRequest.From(null, 25).PageSize);
    }

    [Fact]
    [Proves("ADR-0009")]
    public void APageKnowsItsNeighbours()
    {
        var page = new Page<string>(["a", "b"], new PageRequest(2, 2), 5);

        Assert.Equal(3, page.TotalPages);
        Assert.True(page.HasPrevious);
        Assert.True(page.HasNext);
        Assert.False(new Page<string>(["e"], new PageRequest(3, 2), 5).HasNext);
        Assert.False(Page<string>.Empty(PageRequest.Default).HasNext);
        Assert.Equal(0, Page<string>.Empty(PageRequest.Default).TotalPages);
    }

    [Fact]
    [Proves("ADR-0009")]
    public void MapKeepsThePagingAndConvertsTheItems()
    {
        var page = new Page<int>([1, 2, 3], new PageRequest(1, 3), 10);

        var mapped = page.Map(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture));

        string[] expected = ["1", "2", "3"];
        Assert.Equal(expected, mapped.Items);
        Assert.Equal(10, mapped.TotalCount);
        Assert.Equal(4, mapped.TotalPages);
    }

    [Fact]
    [Proves("ADR-0009")]
    public void MapKeepsTheCapFlag()
    {
        var capped = new Page<int>([], new PageRequest(1, 999), 0);

        Assert.True(capped.Map(i => i).WasCapped);
    }

    [Fact]
    [Proves("ADR-0009")]
    public void ANegativeTotalIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Page<int>([], PageRequest.Default, -1));
    }

    [Fact]
    [Proves("ADR-0009")]
    public void TheDefaultListQueryHasNoTerms()
    {
        var query = ListQuery.Default;

        Assert.Equal(PageRequest.Default, query.Page);
        Assert.Empty(query.Sorts);
        Assert.Empty(query.Filters);
        Assert.Empty(query.Fields);
    }
}
