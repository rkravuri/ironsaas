using FluentValidation;
using Iron.Kernel.Api.Lists;
using Iron.Kernel.Api.Responses;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Api.Tests.Sample;
using Iron.Kernel.Api.Validation;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Api.Tests;

/// <summary>A small API built with the conventions, served in memory. One instance for all tests in the "api" collection.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddIronApi();
        builder.Services.AddScoped<IValidator<CreateThing>, CreateThingValidator>();

        var app = builder.Build();
        app.UseIronTelemetry();
        app.UseIronApi();

        var things = app.MapApiGroup("things").AllowAnonymous(); // the sample tests the list grammar; permissions have their own tests
        things.MapGet("/", (HttpContext http) =>
        {
            var parsed = ListQueryParser.Parse(http.Request.Query, Things.Fields);
            if (parsed.IsFailure)
            {
                return ApiResults.Problem(parsed.Error, http);
            }

            var query = parsed.Value;
            var filtered = Things.All.AsQueryable().ApplyListQuery(query, Things.Fields);
            var items = filtered.Skip(query.Page.Skip).Take(query.Page.PageSize).ToList();
            return ApiResults.List(new Page<Thing>(items, query.Page, filtered.LongCount()), http, query);
        });
        things.MapPost("/", (CreateThing request) => TypedResults.Created("/api/v1/things/new", request)).Validate<CreateThing>();
        things.MapGet("/missing", (HttpContext http) => ApiResults.Problem(Error.NotFound("tests.thing.not_found", "There is no such thing."), http));
        things.MapGet("/boom", () =>
        {
            throw new InvalidOperationException("secret detail that must not leak");
        });

        await app.StartAsync(TestContext.Current.CancellationToken);
        _app = app;
        Client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(TestContext.Current.CancellationToken);
            await _app.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
