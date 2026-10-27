using System.Text.Json;
using FluentValidation;
using Iron.Kernel.Api.Http;
using Iron.Kernel.Api.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Api.Tests.Validation;

public sealed record Part(string Code);

public sealed record Unlimited(string Name, IReadOnlyList<string> Tags, JsonElement Value, Part Part, int Count, Guid Id, bool Flag, DateOnly On);

public sealed class UnlimitedValidator : AbstractValidator<Unlimited>
{
    public UnlimitedValidator()
    {
        RuleFor(u => u.Name).NotEmpty();
    }
}

public sealed record Limited(string Name, IReadOnlyList<string> Tags, JsonElement? Value, Part Part, IReadOnlyList<Part> Parts, IReadOnlyList<Part> More, Part? Optional, int Count);

public sealed class PartValidator : AbstractValidator<Part>
{
    public PartValidator()
    {
        RuleFor(p => p.Code).NotEmpty().MaximumLength(10);
    }
}

public sealed class LimitedValidator : AbstractValidator<Limited>
{
    public LimitedValidator()
    {
        RuleFor(l => l.Name).MaximumLength(50);
        RuleFor(l => l.Tags).MaximumCount(5);
        RuleForEach(l => l.Tags).MaximumLength(20);
        RuleFor(l => l.Value).MaximumJsonSize(1024);
        RuleFor(l => l.Part).SetValidator(new PartValidator());
        RuleFor(l => l.Parts).MaximumCount(5);
        RuleForEach(l => l.Parts).SetValidator(new PartValidator());
        RuleFor(l => l.More).MaximumCount(5);
        RuleForEach(l => l.More).ChildRules(part => part.RuleFor(p => p.Code).MaximumLength(10));
        RuleFor(l => l.Optional!).SetValidator(new PartValidator());
    }
}

public sealed record OnlyTyped(Guid Id, bool Flag, DateTimeOffset At, int? Count);

/// <summary>
/// TF-071 AC1 (ADR-0053): every field of a request body has a declared limit, and the host does not start otherwise. The
/// check reads each validator's rules, so a field without a limit fails the build through the host tests.
/// </summary>
public class RequestLimitCheckTests
{
    private static readonly Func<Type, IValidator?> NoneRegistered = _ => null;

    [Fact]
    [Proves("TF-071 AC1", "ADR-0053")]
    public void AStringWithoutAMaximumLengthIsCaughtAndSoIsEachFieldOfAnotherKindWithoutItsLimit()
    {
        var problems = RequestLimitCheck.Check(typeof(Unlimited), new UnlimitedValidator(), NoneRegistered);

        Assert.Equal(
            [
                "name is a string without a maximum length. Add .MaximumLength(n).",
                "tags is a list without a maximum count. Add .MaximumCount(n).",
                "tags[] is a string without a maximum length. Add .MaximumLength(n).",
                "value is a JSON value without a maximum size. Add .MaximumJsonSize(bytes).",
                "part is an object without a validator of its own. Add .SetValidator(...) with a limit for each of its fields.",
            ],
            problems);
    }

    [Fact]
    [Proves("TF-071 AC1", "ADR-0053")]
    public void LimitsDeclaredOnTheFieldOnEachItemAndInANestedValidatorAreRead()
    {
        Assert.Empty(RequestLimitCheck.Check(typeof(Limited), new LimitedValidator(), NoneRegistered));
    }

    [Fact]
    [Proves("TF-071 AC1", "ADR-0053")]
    public async Task AHostWithABodyItDoesNotLimitDoesNotStartAndTheMessageNamesTheEndpointAndTheField()
    {
        await using var app = Build(services => services.AddScoped<IValidator<Unlimited>, UnlimitedValidator>());
        app.MapPost("/api/v1/unlimited", (Unlimited request) => "ok").AllowAnonymous().Validate<Unlimited>();
        app.MapPost("/api/v1/unvalidated", (Part request) => "ok").AllowAnonymous();
        app.MapPost("/api/v1/typed", (OnlyTyped request) => "ok").AllowAnonymous();
        app.MapPost("/api/v1/raw", (JsonElement body) => "ok").AllowAnonymous();
        app.MapPost("/api/v1/raw-limited", (JsonElement body) => "ok").AllowAnonymous().LimitBodySize(4096);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("POST /api/v1/unlimited: Unlimited.name is a string without a maximum length", exception.Message, StringComparison.Ordinal);
        Assert.Contains("POST /api/v1/unvalidated: takes Part without validating it", exception.Message, StringComparison.Ordinal);
        Assert.Contains("POST /api/v1/raw: reads its body itself without a declared size", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/v1/typed", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/v1/raw-limited", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-071 AC1", "ADR-0053")]
    public async Task AHostWhoseBodiesAreAllLimitedStarts()
    {
        await using var app = Build(services => services.AddScoped<IValidator<Limited>, LimitedValidator>());
        app.MapPost("/api/v1/limited", (Limited request) => "ok").AllowAnonymous().Validate<Limited>();

        await app.StartAsync(TestContext.Current.CancellationToken);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private static WebApplication Build(Action<IServiceCollection> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddIronApi();
        configure(builder.Services);
        var app = builder.Build();
        app.UseIronApi();
        return app;
    }
}
