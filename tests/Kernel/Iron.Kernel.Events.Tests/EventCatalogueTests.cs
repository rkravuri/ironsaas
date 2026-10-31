using Iron.Kernel.Api.Modules;
using Iron.Kernel.Events.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Iron.Kernel.Events.Tests;

/// <summary>The declarations of events and the catalogue that lists them (TF-110), and what a declaration refuses.</summary>
public class EventCatalogueTests
{
    [Fact]
    [Proves("TF-110 AC1")]
    public void ADeclaredEventIsInTheCatalogueWithItsSchema()
    {
        var catalogue = LicensingCatalogue.From([new ProbeModule()]);

        var entry = catalogue.FindEvent("probe.order.placed", 2);

        Assert.NotNull(entry);
        Assert.Equal("probe", entry.Module);
        Assert.Equal("probe_order", entry.RecordType);
        Assert.Equal("Probe owners", entry.Owner);
        Assert.Equal(
        [
            new EventField("order_id", EventFieldType.Identifier, Required: true, Personal: false),
            new EventField("code", EventFieldType.Code, Required: true, Personal: false),
            new EventField("sequence", EventFieldType.Integer, Required: true, Personal: false),
            new EventField("placed_by", EventFieldType.Identifier, Required: true, Personal: true),
            new EventField("channel", EventFieldType.Code, Required: false, Personal: false),
        ], entry.Fields);

        // The file the control plane carries keeps it, field by field, and reads it back.
        var json = catalogue.ToJson();
        Assert.Contains("\"name\": \"probe.order.placed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"identifier\"", json, StringComparison.Ordinal);
        var parsed = LicensingCatalogue.Parse(json);
        Assert.Equal(entry.Fields, parsed.FindEvent("probe.order.placed", 2)!.Fields);
        Assert.Equal([ProbeModule.Counter, ProbeModule.Flaky, ProbeModule.Sequencer], parsed.FindEvent("probe.order.placed", 1)!.Subscribers);
    }

    [Fact]
    [Proves("TF-110 AC3")]
    public void BothVersionsAreListedWithTheDeprecationDateOfTheOld()
    {
        var catalogue = LicensingCatalogue.Parse(LicensingCatalogue.From([new ProbeModule()]).ToJson());

        var old = catalogue.FindEvent("probe.order.placed", 1)!;
        var current = catalogue.FindEvent("probe.order.placed", 2)!;

        Assert.Equal(ProbeModule.V1Ends, old.DeprecatedOn);
        Assert.Equal(2, old.ReplacedBy);
        Assert.Null(current.DeprecatedOn);
        Assert.Equal(["probe.order.placed", "probe.order.placed"], catalogue.Events.Where(e => e.Module == "probe").Select(e => e.Name));
    }

    [Fact]
    [Proves("TF-110 AC2")]
    public void DataThatDoesNotFitItsDeclarationIsRefused()
    {
        var empty = new OrderPlaced(Guid.Empty, "PO-1", 1, Ids.New());
        var freeText = new OrderPlaced(Ids.New(), "the supplier said no", 1, Ids.New());
        var tooLong = new OrderPlaced(Ids.New(), new string('x', EventType.CodeMaxLength + 1), 1, Ids.New());
        var missing = new OrderPlaced(Ids.New(), null!, 1, Ids.New());

        Assert.Equal(["order_id is an empty identifier."], ProbeModule.Placed.ProblemsOf(empty));
        Assert.Single(ProbeModule.Placed.ProblemsOf(freeText));
        Assert.Single(ProbeModule.Placed.ProblemsOf(tooLong));
        Assert.Equal(["code is required."], ProbeModule.Placed.ProblemsOf(missing));
        var refused = Assert.Throws<EventSchemaException>(() => ProbeModule.Placed.Serialize(freeText));
        Assert.Contains("probe.order.placed v1", refused.Message, StringComparison.Ordinal);
        Assert.Empty(ProbeModule.Placed.ProblemsOf(new OrderPlaced(Ids.New(), "PO-1", 1, Ids.New())));
    }

    [Fact]
    [Proves("ADR-0060")]
    public void AnEventCarriesIdentifiersAndCodesNeverACopyOfARecordNorAPersonsDetails()
    {
        var copy = Assert.Throws<InvalidOperationException>(() => new EventType<WithACopy>("probe.order.copied", 1, "probe_order", "Carries a record.", "Probe owners"));
        Assert.Contains("never a copy of a record", copy.Message, StringComparison.Ordinal);

        var person = Assert.Throws<InvalidOperationException>(() => new EventType<WithAName>("probe.order.named", 1, "probe_order", "Names a person.", "Probe owners"));
        Assert.Contains("names a person but is not an identifier", person.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => new EventType<OrderPlaced>("ProbeOrderPlaced", 1, "probe_order", "Not a name.", "Probe owners"));
    }

    [Fact]
    [Proves("ADR-0060")]
    public void AModuleDeclaresOnlyItsOwnEventsAndSubscriptions()
    {
        var foreign = Assert.Throws<InvalidOperationException>(() => new EventDeclarations([new StrayModule(publishes: true)]));
        Assert.Contains("an event's name starts with the name of the module that publishes it", foreign.Message, StringComparison.Ordinal);

        var subscription = Assert.Throws<InvalidOperationException>(() => new EventDeclarations([new ProbeModule(), new StrayModule(publishes: false)]));
        Assert.Contains("a subscription's name starts with the name of the module that holds it", subscription.Message, StringComparison.Ordinal);

        var declarations = new EventDeclarations([new ProbeModule()]);
        Assert.Equal([ProbeModule.Counter, ProbeModule.Flaky, ProbeModule.Sequencer], declarations.SubscribersOf("probe.order.placed", 1).Select(s => s.Name));
        Assert.Empty(declarations.SubscribersOf("probe.order.placed", 2));
        Assert.Empty(declarations.Unmatched);
    }

    [Fact]
    [Proves("TF-113 AC3")]
    public void WithNoBrokerTheInProcessDispatcherIsUsed()
    {
        var services = new ServiceCollection().AddEventDispatch();

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EventDispatchWorker));
    }

    public sealed record Line(string Item, int Quantity);

    public sealed record WithACopy(Guid OrderId, IReadOnlyList<Line> Lines);

    public sealed record WithAName(Guid OrderId, [property: PersonalData] string BuyerName);

    /// <summary>A module that publishes another's event, or holds a subscription in another's name.</summary>
    private sealed class StrayModule(bool publishes) : IModule
    {
        public string Name => "stray";

        public IReadOnlyList<Permission> Permissions => [];

        public IReadOnlyList<EventType> Events => publishes ? [ProbeModule.PlacedV2] : [];

        public IReadOnlyList<EventSubscription> Subscriptions => publishes ? [] : [EventSubscription.To<OrderPlaced, CountingHandler>(ProbeModule.Placed, "probe.someone_elses", "In the probe's name.")];

        public void AddServices(IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
        }

        public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
        {
        }
    }
}
