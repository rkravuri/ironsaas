using System.Collections.Concurrent;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.History;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Events.Tests;

/// <summary>An order placed in the probe module: what its declaration reads, and the person who placed it by identifier only.</summary>
public sealed record OrderPlaced(Guid OrderId, string Code, int Sequence, [property: PersonalData] Guid PlacedBy);

/// <summary>The same event's second version, with a channel; the first is published beside it until its deprecation day.</summary>
public sealed record OrderPlacedV2(Guid OrderId, string Code, int Sequence, [property: PersonalData] Guid PlacedBy, string? Channel);

/// <summary>A probe module that publishes orders and holds three subscriptions, as a module and its subscribers would (ADR-0060).</summary>
public sealed class ProbeModule : IModule
{
    public static readonly DateOnly V1Ends = new(2027, 1, 31);

    public static readonly EventType<OrderPlaced> Placed = new EventType<OrderPlaced>("probe.order.placed", 1, "probe_order", "An order was placed in the probe.", "Probe owners").Deprecated(V1Ends, replacedBy: 2);

    public static readonly EventType<OrderPlacedV2> PlacedV2 = new("probe.order.placed", 2, "probe_order", "An order was placed in the probe, with its channel.", "Probe owners");

    public const string Counter = "probe.count_orders";
    public const string Flaky = "probe.flaky";
    public const string Sequencer = "probe.record_sequence";

    public const string Counted = "probe.order_counted";
    public const string FlakyTried = "probe.flaky_tried";
    public const string FlakyHandled = "probe.flaky_handled";

    public string Name => "probe";

    public IReadOnlyList<Permission> Permissions => [];

    public IReadOnlyList<EventType> Events => [Placed, PlacedV2];

    public IReadOnlyList<EventSubscription> Subscriptions =>
    [
        EventSubscription.To<OrderPlaced, CountingHandler>(Placed, Counter, "Counts each order once."),
        EventSubscription.To<OrderPlaced, FlakyHandler>(Placed, Flaky, "Fails as the test asks, then handles."),
        EventSubscription.To<OrderPlaced, SequenceHandler>(Placed, Sequencer, "Records the order in which it handles each record's events."),
    ];

    public IReadOnlyList<AuditEventType> AuditEventTypes => [new(Counted, "Probe order counted"), new(FlakyTried, "Probe flaky handler tried"), new(FlakyHandled, "Probe flaky handler handled")];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

/// <summary>
/// A module of a probe suite, which reads the probe's orders, as a suite's module reads a Foundation event: a unit runs it
/// only where the suite is installed, and its waiting deliveries are held while the suite is removed (ADR-0060).
/// </summary>
public sealed class ProbeSuiteModule : IModule
{
    public const string SuiteShortName = "probesuite";
    public const string Subscriber = "probesuite.count_orders";

    public string Name => "probesuite";

    public string Suite => SuiteShortName;

    // A suite's module is licensed, as every one is (ADR-0034, ADR-0043).
    public ModuleLicence? Licence { get; } = new("probesuite", SubscriptionType.Platform);

    public IReadOnlyList<Permission> Permissions => [];

    public IReadOnlyList<EventSubscription> Subscriptions =>
    [
        EventSubscription.To<OrderPlaced, SuiteCountingHandler>(ProbeModule.Placed, Subscriber, "Counts each order in the suite."),
    ];

    public static SuiteDefinition Definition() => new(SuiteShortName, "Probe suite", "A suite the events tests install and remove.", () => [new ProbeSuiteModule()]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

public sealed class SuiteCountingHandler(ProbeBehaviour behaviour) : IEventHandler<OrderPlaced>
{
    public Task<Result> HandleAsync(DomainEvent<OrderPlaced> domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        behaviour.SuiteCounted.Enqueue(domainEvent.Data.OrderId);
        return Task.FromResult(Result.Success());
    }
}

/// <summary>What the probe's handlers do, which a test sets: how many times the flaky one fails, how, and how long the sequencer waits.</summary>
public sealed class ProbeBehaviour
{
    /// <summary>How many attempts of the flaky handler fail before one succeeds; a negative number, every one.</summary>
    public int FlakyFailures { get; set; }

    /// <summary>Whether a failure throws, rather than answering a failed result.</summary>
    public bool Throws { get; set; }

    public int SequenceDelayMilliseconds { get; set; }

    public int FlakyAttempts => _flakyAttempts;

    public ConcurrentQueue<(Guid Record, int Sequence)> Sequence { get; } = new();

    /// <summary>The orders the probe suite's subscriber counted, once each.</summary>
    public ConcurrentQueue<Guid> SuiteCounted { get; } = new();

    private int _flakyAttempts;

    public int NextFlakyAttempt() => Interlocked.Increment(ref _flakyAttempts);
}

public sealed class CountingHandler(IAuditWriter audit) : IEventHandler<OrderPlaced>
{
    public async Task<Result> HandleAsync(DomainEvent<OrderPlaced> domainEvent, CancellationToken cancellationToken)
    {
        await audit.WriteAsync(new AuditEvent(ProbeModule.Counted, AuditOutcome.Success, SystemPrincipal.SystemId, domainEvent.Data.Code, RecordType: "probe_order", RecordId: domainEvent.Data.OrderId), cancellationToken);
        return Result.Success();
    }
}

/// <summary>Writes that it tried before it fails, so a test sees the failed attempt's work rolled back.</summary>
public sealed class FlakyHandler(IAuditWriter audit, ProbeBehaviour behaviour) : IEventHandler<OrderPlaced>
{
    public async Task<Result> HandleAsync(DomainEvent<OrderPlaced> domainEvent, CancellationToken cancellationToken)
    {
        var attempt = behaviour.NextFlakyAttempt();
        await audit.WriteAsync(new AuditEvent(ProbeModule.FlakyTried, AuditOutcome.Success, SystemPrincipal.SystemId, null, RecordType: "probe_order", RecordId: domainEvent.Data.OrderId), cancellationToken);
        if (behaviour.FlakyFailures < 0 || attempt <= behaviour.FlakyFailures)
        {
            if (behaviour.Throws)
            {
                throw new InvalidOperationException($"The probe failed attempt {attempt} on purpose.");
            }

            return Error.Unavailable("probe.flaky.failed", $"The probe failed attempt {attempt} on purpose.");
        }

        await audit.WriteAsync(new AuditEvent(ProbeModule.FlakyHandled, AuditOutcome.Success, SystemPrincipal.SystemId, null, RecordType: "probe_order", RecordId: domainEvent.Data.OrderId), cancellationToken);
        return Result.Success();
    }
}

public sealed class SequenceHandler(ProbeBehaviour behaviour) : IEventHandler<OrderPlaced>
{
    public async Task<Result> HandleAsync(DomainEvent<OrderPlaced> domainEvent, CancellationToken cancellationToken)
    {
        if (behaviour.SequenceDelayMilliseconds > 0)
        {
            await Task.Delay(Random.Shared.Next(behaviour.SequenceDelayMilliseconds), cancellationToken);
        }

        behaviour.Sequence.Enqueue((domainEvent.Data.OrderId, domainEvent.Data.Sequence));
        return Result.Success();
    }
}
