using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Api.Tests.Sample;

namespace Iron.Kernel.Api.Tests;

[Collection(ApiCollection.Name)]
public class ProblemTests(ApiFixture fixture)
{
    [Fact]
    [Proves("TF-100 AC4", "ADR-0009")]
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
    [Proves("TF-068 AC1", "ADR-0052")]
    public void AChangeHeldForASecondAdministratorAnswers202WithItsIdNotAFailure()
    {
        var change = Ids.New();
        var held = new Error(ErrorKind.Held, "changes.change.held", "Your change waits for a second administrator's approval.")
        {
            Extensions = new Dictionary<string, object>(StringComparer.Ordinal) { ["change_id"] = change },
        };

        var problem = Iron.Kernel.Api.Problems.ProblemFactory.Create(held, null);

        Assert.Equal(202, problem.Status);
        Assert.Equal("changes.change.held", problem.Extensions["error_code"]);
        Assert.Equal(change, problem.Extensions["change_id"]);
        Assert.Equal("The change waits for a second administrator's approval.", problem.Title);
    }

    [Fact]
    [Proves("TF-071 AC1", "ADR-0053")]
    public async Task ARequestOverAFieldsLimitIsRefusedWithAValidationProblemNamingTheField()
    {
        var tooMany = new CreateThing(new string('x', 51), 12, ["a", "b", "c", "d"]);
        var tooLong = new CreateThing("hammer", 12, ["short", new string('y', 21)]);

        using var first = await fixture.Client.PostAsJsonAsync(new Uri("/api/v1/things", UriKind.Relative), tooMany, TestContext.Current.CancellationToken);
        using var second = await fixture.Client.PostAsJsonAsync(new Uri("/api/v1/things", UriKind.Relative), tooLong, TestContext.Current.CancellationToken);
        var tooManyErrors = (await Json(first)).GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("field").GetString(), e.GetProperty("code").GetString())).ToList();
        var tooLongErrors = (await Json(second)).GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("field").GetString(), e.GetProperty("code").GetString())).ToList();

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (first.StatusCode, second.StatusCode));
        Assert.Equal([("name", "maximum_length"), ("tags", "maximum_count")], tooManyErrors);
        Assert.Equal([("tags[1]", "maximum_length")], tooLongErrors);
    }

    [Fact]
    [Proves("TF-071 AC1", "ADR-0053")]
    public async Task AQueryParameterOverItsLimitOrTooManyOfThemAreRefusedNamingTheParameter()
    {
        var parameters = string.Join('&', Enumerable.Range(1, 51).Select(i => $"p{i}=1"));
        using var tooLong = await fixture.Client.GetAsync(new Uri($"/api/v1/things?filter={new string('x', 2001)}", UriKind.Relative), TestContext.Current.CancellationToken);
        using var tooMany = await fixture.Client.GetAsync(new Uri($"/api/v1/things?{parameters}", UriKind.Relative), TestContext.Current.CancellationToken);
        using var fine = await fixture.Client.GetAsync(new Uri($"/api/v1/things?q={new string('x', 2000)}", UriKind.Relative), TestContext.Current.CancellationToken);
        var longError = (await Json(tooLong)).GetProperty("errors")[0];
        var manyError = (await Json(tooMany)).GetProperty("errors")[0];

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (tooLong.StatusCode, tooMany.StatusCode));
        Assert.Equal(("filter", "maximum_length"), (longError.GetProperty("field").GetString(), longError.GetProperty("code").GetString()));
        Assert.Equal(("query", "maximum_count"), (manyError.GetProperty("field").GetString(), manyError.GetProperty("code").GetString()));
        Assert.NotEqual(HttpStatusCode.BadRequest, fine.StatusCode);
    }

    [Fact]
    [Proves("ADR-0009")]
    public async Task AValidBodyPassesTheFilter()
    {
        using var response = await fixture.Client.PostAsJsonAsync(new Uri("/api/v1/things", UriKind.Relative), new CreateThing("hammer", 12), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    [Proves("TF-100 AC1", "ADR-0009")]
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
    [Proves("TF-100 AC1", "ADR-0009")]
    public async Task AnUnknownRouteIsAProblem()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/nothing-here", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("api.route.not_found", json.GetProperty("error_code").GetString());
        Assert.Equal("/api/v1/nothing-here", json.GetProperty("instance").GetString());
    }

    [Fact]
    [Proves("TF-100 AC1", "ADR-0009")]
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
    [Proves("TF-100 AC1", "ADR-0009")]
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
    [Proves("ADR-0010")]
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
