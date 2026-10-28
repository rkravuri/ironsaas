using FluentValidation;
using Iron.Kernel.Api.Validation;

namespace Iron.Kernel.Api.Tests.Sample;

public enum ThingStatus
{
    Draft,
    Active,
    Retired,
}

/// <summary>The response shape of the sample resource. Field names in ThingFields match these properties in snake_case.</summary>
public sealed record Thing(Guid Id, string Name, ThingStatus Status, int Weight, DateTimeOffset CreatedAt);

public sealed record CreateThing(string Name, int Weight, IReadOnlyList<string>? Tags = null);

public sealed class CreateThingValidator : AbstractValidator<CreateThing>
{
    public CreateThingValidator()
    {
        RuleFor(t => t.Name).NotEmpty().MaximumLength(50);
        RuleFor(t => t.Weight).GreaterThan(0);
        RuleFor(t => t.Tags).MaximumCount(3);
        RuleForEach(t => t.Tags).MaximumLength(20);
    }
}

public static class Things
{
    public static readonly ListFields<Thing> Fields = new ListFields<Thing>()
        .Add("id", t => t.Id)
        .Add("name", t => t.Name)
        .Add("status", t => t.Status)
        .Add("weight", t => t.Weight)
        .Add("created_at", t => t.CreatedAt, FieldUse.Select | FieldUse.Sort)
        .SortByDefault("name");

    public static readonly IReadOnlyList<Thing> All = Build();

    private static List<Thing> Build()
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var names = new[] { "anvil", "bolt", "crank", "drill", "emery" };
        var things = new List<Thing>();
        for (var i = 0; i < names.Length; i++)
        {
            var status = i % 3 == 0 ? ThingStatus.Draft : i % 3 == 1 ? ThingStatus.Active : ThingStatus.Retired;
            things.Add(new Thing(Guid.Parse($"00000000-0000-0000-0000-00000000000{i + 1}"), names[i], status, (i + 1) * 10, start.AddDays(i)));
        }

        return things;
    }
}
