namespace Iron.Kernel.Api.Modules;

/// <summary>
/// The domain events a host's modules publish and the subscriptions they hold (TF-110, TF-111, ADR-0060), registered by
/// AddModules. A unit without a suite has none of the suite's events or subscriptions, since it runs none of its modules
/// (TF-244): an event of a type nobody here declares is kept and handed to nobody, and a subscription to such an event is
/// idle.
/// </summary>
public sealed class EventDeclarations
{
    private readonly Dictionary<(string Name, int Version), EventType> _events = [];
    private readonly Dictionary<string, EventSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _suites = new(StringComparer.Ordinal);

    public EventDeclarations(IEnumerable<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var list = modules.ToList();
        foreach (var module in list)
        {
            foreach (var type in module.Events)
            {
                if (!string.Equals(type.Module, module.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"The module '{module.Name}' declares the event '{type}', but an event's name starts with the name of the module that publishes it.");
                }

                if (!_events.TryAdd((type.Name, type.Version), type))
                {
                    throw new InvalidOperationException($"The event '{type}' is declared twice.");
                }
            }
        }

        foreach (var module in list)
        {
            foreach (var subscription in module.Subscriptions)
            {
                if (!string.Equals(subscription.Module, module.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"The module '{module.Name}' holds the subscription '{subscription.Name}', but a subscription's name starts with the name of the module that holds it.");
                }

                if (!_subscriptions.TryAdd(subscription.Name, subscription))
                {
                    throw new InvalidOperationException($"The subscription '{subscription.Name}' is declared twice.");
                }

                if (module.Suite != SuiteName.Foundation)
                {
                    _suites[subscription.Name] = module.Suite;
                }
            }
        }
    }

    /// <summary>Every event this host's modules publish, by name and version.</summary>
    public IReadOnlyList<EventType> Events => [.. _events.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Version)];

    /// <summary>Every subscription, by name.</summary>
    public IReadOnlyList<EventSubscription> Subscriptions => [.. _subscriptions.Values.OrderBy(s => s.Name, StringComparer.Ordinal)];

    /// <summary>
    /// The subscriptions whose event no module of this host publishes: idle here, since nothing raises their event. A
    /// host that runs only some modules, such as a test's, has them; a tenant's unit has none, which the host tests check.
    /// </summary>
    public IReadOnlyList<EventSubscription> Unmatched => [.. Subscriptions.Where(s => !_events.ContainsKey((s.Event.Name, s.Event.Version)))];

    public EventType? Find(string name, int version) => _events.GetValueOrDefault((name, version));

    public EventSubscription? FindSubscription(string name) => _subscriptions.GetValueOrDefault(name);

    /// <summary>The suite of the module that holds the subscription, or null for the Foundation's (and for one this host does not hold).</summary>
    public string? SuiteOf(string subscription) => _suites.GetValueOrDefault(subscription);

    /// <summary>The subscriptions that read this event version, by name: the subscribers the dispatcher hands it to.</summary>
    public IReadOnlyList<EventSubscription> SubscribersOf(string name, int version) =>
        [.. _subscriptions.Values.Where(s => s.Event.Name == name && s.Event.Version == version).OrderBy(s => s.Name, StringComparer.Ordinal)];
}
