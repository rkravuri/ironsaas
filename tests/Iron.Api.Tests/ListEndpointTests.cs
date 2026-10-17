using System.Net;
using System.Text.Json;

namespace Iron.Api.Tests;

[Collection(ApiCollection.Name)]
public class ListEndpointTests(ApiFixture fixture)
{
    [Fact]
    public async Task AListReturnsTheEnvelopeInSnakeCase()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/things?page_size=2", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, json.GetProperty("data").GetArrayLength());
        Assert.Equal("anvil", json.GetProperty("data")[0].GetProperty("name").GetString());
        Assert.Equal("draft", json.GetProperty("data")[0].GetProperty("status").GetString());
        Assert.True(json.GetProperty("data")[0].TryGetProperty("created_at", out _));

        var meta = json.GetProperty("meta");
        Assert.Equal(1, meta.GetProperty("page_number").GetInt32());
        Assert.Equal(2, meta.GetProperty("page_size").GetInt32());
        Assert.Equal(5, meta.GetProperty("total_count").GetInt64());
        Assert.Equal(3, meta.GetProperty("total_pages").GetInt32());
        Assert.False(meta.GetProperty("page_size_capped").GetBoolean());

        var links = json.GetProperty("links");
        Assert.Equal("/api/v1/things?page_size=2&page_number=1", links.GetProperty("self").GetString());
        Assert.Equal(JsonValueKind.Null, links.GetProperty("previous").ValueKind);
        Assert.Equal("/api/v1/things?page_size=2&page_number=2", links.GetProperty("next").GetString());
        Assert.Equal("/api/v1/things?page_size=2&page_number=3", links.GetProperty("last").GetString());
    }

    [Fact]
    public async Task APageSizeAboveTheMaximumIsCappedAndReported()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/things?page_size=500", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(200, json.GetProperty("meta").GetProperty("page_size").GetInt32());
        Assert.True(json.GetProperty("meta").GetProperty("page_size_capped").GetBoolean());
        Assert.Equal(200, json.GetProperty("meta").GetProperty("max_page_size").GetInt32());
    }

    [Fact]
    public async Task AnUnknownFilterFieldIsAProblemThatNamesTheField()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/things?filter=colour:eq:red", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("api.list.invalid", json.GetProperty("error_code").GetString());
        Assert.Equal("urn:iron:error:api.list.invalid", json.GetProperty("type").GetString());
        Assert.Equal(32, json.GetProperty("correlation_id").GetString()!.Length);

        var error = json.GetProperty("errors")[0];
        Assert.Equal("filter", error.GetProperty("field").GetString());
        Assert.Equal("unknown_field", error.GetProperty("code").GetString());
        Assert.Contains("colour", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("name", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOperatorTheFieldDoesNotSupportIsNamed()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/things?filter=name:gt:b", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("operator_not_allowed", json.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task FiltersSortsAndFieldsAreApplied()
    {
        using var response = await fixture.Client.GetAsync(
            new Uri("/api/v1/things?filter=status:in:active,retired&filter=weight:gte:30&sort=-weight&fields=id,name", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = json.GetProperty("data");
        // active or retired with weight >= 30: crank (30, retired), emery (50, active); drill (40) is draft.
        Assert.Equal(2, data.GetArrayLength());
        Assert.Equal("emery", data[0].GetProperty("name").GetString());
        Assert.Equal("crank", data[1].GetProperty("name").GetString());
        Assert.False(data[0].TryGetProperty("weight", out _));
        Assert.True(data[0].TryGetProperty("id", out _));
    }

    [Fact]
    public async Task ANameFilterIgnoresCase()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/things?filter=name:contains:RI", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await Json(response);

        var names = json.GetProperty("data").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        string?[] expected = ["drill"];
        Assert.Equal(expected, names);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
