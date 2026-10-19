namespace Iron.Kernel.Tests;

public class ListFieldsTests
{
    private enum Colour
    {
        Red,
        Green,
    }

    private sealed record Item(Guid Id, string Name, int Count, decimal Price, DateTimeOffset CreatedAt, bool Active, Colour Colour, int? Rank);

    [Fact]
    public void OperatorsFollowTheFieldType()
    {
        var fields = new ListFields<Item>()
            .Add("name", i => i.Name)
            .Add("count", i => i.Count)
            .Add("created_at", i => i.CreatedAt)
            .Add("active", i => i.Active)
            .Add("colour", i => i.Colour)
            .Add("id", i => i.Id)
            .Add("rank", i => i.Rank);

        Assert.True(fields.Get("name")!.Allows(FilterOperator.Contains));
        Assert.False(fields.Get("name")!.Allows(FilterOperator.GreaterThan));
        Assert.True(fields.Get("count")!.Allows(FilterOperator.GreaterThanOrEqual));
        Assert.True(fields.Get("rank")!.Allows(FilterOperator.LessThan));
        Assert.True(fields.Get("created_at")!.Allows(FilterOperator.LessThan));
        Assert.False(fields.Get("active")!.Allows(FilterOperator.In));
        Assert.True(fields.Get("colour")!.Allows(FilterOperator.In));
        Assert.True(fields.Get("id")!.Allows(FilterOperator.In));
        Assert.False(fields.Get("id")!.Allows(FilterOperator.Contains));
    }

    [Fact]
    public void UsesLimitWhatAFieldMayDo()
    {
        var fields = new ListFields<Item>()
            .Add("name", i => i.Name)
            .Add("price", i => i.Price, FieldUse.Select)
            .Add("created_at", i => i.CreatedAt, FieldUse.Sort | FieldUse.Select);

        Assert.True(fields.Get("name")!.Allows(FieldUse.Filter));
        Assert.False(fields.Get("price")!.Allows(FieldUse.Sort));
        Assert.False(fields.Get("created_at")!.Allows(FieldUse.Filter));
        string[] sortable = ["name", "created_at"];
        Assert.Equal(sortable, fields.Names(FieldUse.Sort));
    }

    [Fact]
    public void NamesMustBeSnakeCaseAndUnique()
    {
        var fields = new ListFields<Item>().Add("name", i => i.Name);

        Assert.Throws<ArgumentException>(() => fields.Add("Name", i => i.Name));
        Assert.Throws<ArgumentException>(() => fields.Add("created-at", i => i.CreatedAt));
        Assert.Throws<ArgumentException>(() => fields.Add("name", i => i.Count));
    }

    [Fact]
    public void TheDefaultSortMustBeASortableField()
    {
        var fields = new ListFields<Item>()
            .Add("name", i => i.Name)
            .Add("price", i => i.Price, FieldUse.Select);

        Assert.Equal(new SortTerm("name", SortDirection.Descending), fields.SortByDefault("name", SortDirection.Descending).DefaultSort);
        Assert.Throws<ArgumentException>(() => fields.SortByDefault("price"));
        Assert.Throws<ArgumentException>(() => fields.SortByDefault("colour"));
    }

    [Theory]
    [InlineData(typeof(int), "42", true)]
    [InlineData(typeof(int), "4.2", false)]
    [InlineData(typeof(int?), "7", true)]
    [InlineData(typeof(decimal), "19.99", true)]
    [InlineData(typeof(bool), "true", true)]
    [InlineData(typeof(bool), "yes", false)]
    [InlineData(typeof(Guid), "00000000-0000-0000-0000-000000000001", true)]
    [InlineData(typeof(Guid), "not-a-guid", false)]
    [InlineData(typeof(DateTimeOffset), "2026-10-18T10:00:00Z", true)]
    [InlineData(typeof(DateTimeOffset), "yesterday", false)]
    [InlineData(typeof(DateOnly), "2026-10-18", true)]
    [InlineData(typeof(Colour), "green", true)]
    [InlineData(typeof(Colour), "Green", true)]
    [InlineData(typeof(Colour), "blue", false)]
    [InlineData(typeof(Colour), "7", false)]
    [InlineData(typeof(string), "anything", true)]
    public void ValuesAreParsedByType(Type type, string text, bool expected)
    {
        Assert.Equal(expected, ListValues.TryParse(type, text, out var value));
        if (expected)
        {
            Assert.NotNull(value);
        }
    }

    [Theory]
    [InlineData("created_at", true)]
    [InlineData("a1_b2", true)]
    [InlineData("CreatedAt", false)]
    [InlineData("created-at", false)]
    [InlineData("_private", false)]
    public void SnakeCaseNames(string name, bool expected)
    {
        Assert.Equal(expected, Naming.IsSnakeCase(name));
    }

    [Theory]
    [InlineData("purchase-orders", true)]
    [InlineData("users", true)]
    [InlineData("purchase_orders", false)]
    [InlineData("PurchaseOrders", false)]
    public void KebabCaseNames(string name, bool expected)
    {
        Assert.Equal(expected, Naming.IsKebabCase(name));
    }
}
