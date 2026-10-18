using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Tests.Sample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Data.Tests;

public class ListQueryExtensionsRuleTests
{
    private static readonly Guid Organisation = Ids.New();

    private static readonly ListFields<Widget> Fields = new ListFields<Widget>()
        .Add("id", w => w.Id)
        .Add("name", w => w.Name)
        .Add("sharing_mode", w => w.SharingMode)
        .Add("version", w => w.Version)
        .Add("owning_organisation_id", w => w.OwningOrganisationId)
        .SortByDefault("name");

    private static IQueryable<Widget> Widgets()
    {
        return new List<Widget>
        {
            new(Organisation, "anvil"),
            new(Organisation, "bolt"),
            new(Organisation, "crank"),
            new(Ids.New(), "drill"),
        }.AsQueryable();
    }

    [Fact]
    public void FiltersCombineWithAnd()
    {
        var query = ListQuery.Default with
        {
            Filters = [new FilterTerm("name", FilterOperator.Contains, "R"), new FilterTerm("owning_organisation_id", FilterOperator.Equals, Organisation.ToString())],
        };

        var names = Widgets().ApplyListQuery(query, Fields).Select(w => w.Name).ToList();

        // drill also contains an r but belongs to another organisation.
        string[] expected = ["crank"];
        Assert.Equal(expected, names);
    }

    [Fact]
    public void TextFiltersIgnoreCase()
    {
        var query = ListQuery.Default with { Filters = [new FilterTerm("name", FilterOperator.StartsWith, "A")] };

        var names = Widgets().ApplyListQuery(query, Fields).Select(w => w.Name).ToList();

        string[] expected = ["anvil"];
        Assert.Equal(expected, names);
    }

    [Fact]
    public void InMatchesAnyOfTheValuesIncludingEnums()
    {
        var query = ListQuery.Default with { Filters = [new FilterTerm("sharing_mode", FilterOperator.In, "private,global")] };

        Assert.Equal(4, Widgets().ApplyListQuery(query, Fields).Count());
    }

    [Fact]
    public void TheDefaultSortAppliesWhenNoneIsGiven()
    {
        var names = Widgets().ApplyListQuery(ListQuery.Default, Fields).Select(w => w.Name).ToList();

        string[] expected = ["anvil", "bolt", "crank", "drill"];
        Assert.Equal(expected, names);
    }

    [Fact]
    public void SortTermsApplyInOrder()
    {
        var query = ListQuery.Default with
        {
            Sorts = [new SortTerm("owning_organisation_id", SortDirection.Ascending), new SortTerm("name", SortDirection.Descending)],
        };

        var sorted = Widgets().ApplyListQuery(query, Fields).ToList();

        Assert.Equal(sorted.OrderBy(w => w.OwningOrganisationId).ThenByDescending(w => w.Name, StringComparer.Ordinal).Select(w => w.Name), sorted.Select(w => w.Name));
    }

    [Fact]
    public void AFieldOutsideTheListIsRefused()
    {
        var query = ListQuery.Default with { Filters = [new FilterTerm("colour", FilterOperator.Equals, "red")] };

        Assert.Throws<ArgumentException>(() => Widgets().ApplyListQuery(query, Fields));
    }
}

[Collection(PostgresCollection.Name)]
public class ListQueryExtensionsDatabaseTests(PostgresFixture fixture)
{
    private static readonly ListFields<Widget> Fields = new ListFields<Widget>()
        .Add("name", w => w.Name)
        .Add("owning_organisation_id", w => w.OwningOrganisationId)
        .SortByDefault("name");

    [Fact]
    public async Task ToPageAsyncReturnsOnePageWithTheTotal()
    {
        var organisation = Ids.New();
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new FixedPrincipal(Ids.New()));

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            for (var i = 0; i < 7; i++)
            {
                context.Widgets.Add(new Widget(organisation, $"page-{i}"));
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            var query = new ListQuery(
                new PageRequest(2, 3),
                [],
                [new FilterTerm("owning_organisation_id", FilterOperator.Equals, organisation.ToString()), new FilterTerm("name", FilterOperator.StartsWith, "PAGE-")],
                []);

            var page = await context.Widgets.ApplyListQuery(query, Fields).ToPageAsync(query.Page, TestContext.Current.CancellationToken);

            Assert.Equal(7, page.TotalCount);
            Assert.Equal(3, page.TotalPages);
            Assert.Equal(2, page.PageNumber);
            string[] expected = ["page-3", "page-4", "page-5"];
            Assert.Equal(expected, page.Items.Select(w => w.Name));
        }
    }
}
