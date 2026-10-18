using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Iron.Api.Tests.Sample;

namespace Iron.Api.Tests;

[Collection(ApiCollection.Name)]
public class ProblemTests(ApiFixture fixture)
{
    [Fact]
    public async Task AnInvalidBodyReturnsFieldErrorsInSnakeCase()
    {
        using var response = await fixture.Client.PostAsJsonAsync(new Uri("/api/v1/things", UriKind.Relative), new CreateThing("", 0), TestContext.Current.CancellationToken);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("api.validation.failed", json.GetProperty("error_code").GetString());
        var errors = json.GetProperty("errors").EnumerateArray().ToList();
        Assert.Contains(errors, e => e.GetProperty("field").GetString() == "name" && e.GetProperty("code").GetString() == "not_empty");
        Assert.Contains(errors, e => e.GetProperty("field").GetString() == "weight" && e.GetProperty("code").GetString() == "greater_than");
    }

    [Fact]
    public async Task AValidBodyPassesTheFilter()
    {
        using var response = await fixture.Client.PostAsJsonAsync(new Uri("/api/v1/things", UriKind.Relative), new CreateThing("hammer", 12), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJsonIsAProblemWithACorrelationId()
    {
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync(new Uri("/api/v1/things", UriKind.Relative), content, TestContext.Current.CancellationToken);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("api.request.malformed", json.GetProperty("error_code").GetString());
        Assert.Equal(32, json.GetProperty("correlation_id").GetString()!.Length);
    }

    [Fact]
    public async Task AnUnknownRouteIsAProblem()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/nothing-here", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("api.route.not_found", json.GetProperty("error_code").GetString());
        Assert.Equal("/api/v1/nothing-here", json.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task AServiceErrorMapsToItsStatusAndType()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/things/missing", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("urn:iron:error:tests.thing.not_found", json.GetProperty("type").GetString());
        Assert.Equal("tests.thing.not_found", json.GetProperty("error_code").GetString());
        Assert.Equal("There is no such thing.", json.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AnUnhandledExceptionIsA500ProblemWithoutInternals()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/things/boom", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("platform.unexpected", json.GetProperty("error_code").GetString());
        Assert.Equal(32, json.GetProperty("correlation_id").GetString()!.Length);
        Assert.DoesNotContain("secret detail", json.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCorrelationIdInTheBodyIsTheOneInTheHeader()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/things/missing", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await Json(response);

        var header = Assert.Single(response.Headers.GetValues("X-Correlation-Id"));
        Assert.Equal(header, json.GetProperty("correlation_id").GetString());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
