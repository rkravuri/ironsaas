using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Iron.Kernel;

/// <summary>
/// Marks a field of an event's data that names a person (TF-110, ADR-0060). Only an identifier may: an event never
/// carries a name, an address or free text, so a person's data stays in the module that owns it.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PersonalDataAttribute : Attribute;

/// <summary>What a field of an event's data holds: identifiers, codes, numbers, flags and times, never free text.</summary>
public enum EventFieldType
{
    Identifier,
    Identifiers,
    Code,
    Codes,
    Integer,
    Number,
    Boolean,
    Time,
    Day,
}

/// <summary>A field of an event's data as the catalogue lists it: its name in the payload, its type, whether it must be there, whether it names a person.</summary>
public sealed record EventField(string Name, EventFieldType Type, bool Required, bool Personal);

/// <summary>Data that does not match its event's declaration: a programmer's error, refused before anything is written (TF-110 AC2).</summary>
public sealed class EventSchemaException : InvalidOperationException
{
    public EventSchemaException()
    {
    }

    public EventSchemaException(string message)
        : base(message)
    {
    }

    public EventSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A domain event a module publishes, declared in its Contracts and listed in its <c>IModule.Events</c> (TF-110,
/// ADR-0060): a name, <c>module.record.happened</c>; a version; the record type it is about; the shape of its data, read
/// from the data's record type; what it means; who owns it; and, once a newer version replaces it, the day it is no longer
/// published. Adding an optional field keeps the version; any other change is a new version, and the module publishes
/// both until no subscriber reads the old one. An event carries identifiers, not copies of records.
/// </summary>
public abstract partial class EventType
{
    /// <summary>The longest code a field holds, and the most items a list holds.</summary>
    public const int CodeMaxLength = 100;

    public const int ListMaxCount = 100;

    /// <summary>How event data is written and read: snake_case names, enumerations as their snake_case names.</summary>
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly IReadOnlyList<(EventField Field, PropertyInfo Property)> _properties;

    protected EventType(string name, int version, string recordType, string description, string owner, Type dataType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(dataType);
        if (!EventName().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an event name: lowercase words, the module first, then the record and what happened, such as identity.person.deactivated.", nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (!RecordTypeName().IsMatch(recordType))
        {
            throw new ArgumentException($"'{recordType}' is not a record type: lowercase words joined by underscores, such as principal or purchase_order.", nameof(recordType));
        }

        Name = name;
        Version = version;
        RecordType = recordType;
        Description = description;
        Owner = owner;
        DataType = dataType;
        _properties = PropertiesOf(dataType);
        Fields = [.. _properties.Select(p => p.Field)];
    }

    /// <summary><c>module.record.happened</c>.</summary>
    public string Name { get; }

    public int Version { get; }

    /// <summary>The record type the event is about, as record-scoped assignments and the audit trail name it.</summary>
    public string RecordType { get; }

    public string Description { get; }

    /// <summary>The team or role that answers for the event's contract.</summary>
    public string Owner { get; }

    public Type DataType { get; }

    public IReadOnlyList<EventField> Fields { get; }

    /// <summary>The last day this version is published, once a newer version replaces it (TF-110 AC3).</summary>
    public DateOnly? DeprecatedOn { get; private set; }

    /// <summary>The version that replaces this one.</summary>
    public int? ReplacedBy { get; private set; }

    /// <summary>The module that declares it: the first word of its name.</summary>
    public string Module => Name[..Name.IndexOf('.', StringComparison.Ordinal)];

    public override string ToString() => $"{Name} v{Version}";

    /// <summary>What is wrong with this data against the declaration; empty when it fits (TF-110 AC2).</summary>
    public IReadOnlyList<string> ProblemsOf(object data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.GetType() != DataType)
        {
            return [$"{this} carries {DataType.Name}, not {data.GetType().Name}."];
        }

        var problems = new List<string>();
        foreach (var (field, property) in _properties)
        {
            var value = property.GetValue(data);
            if (value is null)
            {
                if (field.Required)
                {
                    problems.Add($"{field.Name} is required.");
                }

                continue;
            }

            switch (field.Type)
            {
                case EventFieldType.Identifier when field.Required && (Guid)value == Guid.Empty:
                    problems.Add($"{field.Name} is an empty identifier.");
                    break;
                case EventFieldType.Identifiers:
                    var identifiers = ((IEnumerable)value).Cast<Guid>().ToList();
                    if (identifiers.Count > ListMaxCount)
                    {
                        problems.Add($"{field.Name} holds {identifiers.Count} identifiers; an event carries at most {ListMaxCount}.");
                    }

                    if (identifiers.Contains(Guid.Empty))
                    {
                        problems.Add($"{field.Name} holds an empty identifier.");
                    }

                    break;
                case EventFieldType.Code when value is string code && !IsCode(code):
                    problems.Add($"{field.Name} is not a code: at most {CodeMaxLength} letters, digits and _.:- with no spaces, never free text.");
                    break;
                case EventFieldType.Codes:
                    var codes = ((IEnumerable)value).Cast<string?>().ToList();
                    if (codes.Count > ListMaxCount)
                    {
                        problems.Add($"{field.Name} holds {codes.Count} codes; an event carries at most {ListMaxCount}.");
                    }

                    if (codes.Any(c => c is null || !IsCode(c)))
                    {
                        problems.Add($"{field.Name} holds a value that is not a code: at most {CodeMaxLength} letters, digits and _.:- with no spaces.");
                    }

                    break;
            }
        }

        return problems;
    }

    /// <summary>The data as the outbox keeps it, once it fits the declaration.</summary>
    public JsonElement Serialize(object data)
    {
        var problems = ProblemsOf(data);
        if (problems.Count > 0)
        {
            throw new EventSchemaException($"The data of {this} does not fit its declaration: {string.Join(" ", problems)}");
        }

        return JsonSerializer.SerializeToElement(data, DataType, Json);
    }

    /// <summary>The data as a subscriber reads it.</summary>
    public object Deserialize(JsonElement data) =>
        JsonSerializer.Deserialize(data, DataType, Json) ?? throw new EventSchemaException($"The data of {this} is empty.");

    /// <summary>Marks this version as replaced: published until <paramref name="on"/>, then only <paramref name="replacedBy"/> (TF-110 AC3).</summary>
    protected void Deprecate(DateOnly on, int replacedBy)
    {
        if (DeprecatedOn is not null)
        {
            throw new InvalidOperationException($"{this} is deprecated already.");
        }

        if (replacedBy <= Version)
        {
            throw new ArgumentOutOfRangeException(nameof(replacedBy), $"{this} is replaced by a later version, not {replacedBy}.");
        }

        DeprecatedOn = on;
        ReplacedBy = replacedBy;
    }

    private static bool IsCode(string value) => value.Length <= CodeMaxLength && CodeValue().IsMatch(value);

    private static IReadOnlyList<(EventField, PropertyInfo)> PropertiesOf(Type dataType)
    {
        var nullability = new NullabilityInfoContext();
        var fields = new List<(EventField, PropertyInfo)>();
        foreach (var property in dataType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = property.PropertyType;
            var underlying = Nullable.GetUnderlyingType(type);
            var nullable = underlying is not null || (!type.IsValueType && nullability.Create(property).ReadState == NullabilityState.Nullable);
            var kind = (underlying ?? type) switch
            {
                var t when t == typeof(Guid) => EventFieldType.Identifier,
                var t when t == typeof(string) || t.IsEnum => EventFieldType.Code,
                var t when t == typeof(int) || t == typeof(long) => EventFieldType.Integer,
                var t when t == typeof(decimal) => EventFieldType.Number,
                var t when t == typeof(bool) => EventFieldType.Boolean,
                var t when t == typeof(DateTimeOffset) => EventFieldType.Time,
                var t when t == typeof(DateOnly) => EventFieldType.Day,
                var t when typeof(IEnumerable<Guid>).IsAssignableFrom(t) => EventFieldType.Identifiers,
                var t when typeof(IEnumerable<string>).IsAssignableFrom(t) => EventFieldType.Codes,
                _ => throw new InvalidOperationException($"{dataType.Name}.{property.Name} is a {type.Name}: an event carries identifiers, codes, numbers, flags, times and lists of identifiers or codes, never a copy of a record (ADR-0060)."),
            };
            var personal = property.GetCustomAttribute<PersonalDataAttribute>() is not null;
            if (personal && kind is not (EventFieldType.Identifier or EventFieldType.Identifiers))
            {
                throw new InvalidOperationException($"{dataType.Name}.{property.Name} names a person but is not an identifier: an event names people by identifier only (TF-110).");
            }

            fields.Add((new EventField(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name), kind, !nullable, personal), property));
        }

        if (fields.Count == 0)
        {
            throw new InvalidOperationException($"{dataType.Name} has no public properties: an event's data names at least what it is about.");
        }

        return fields;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*){2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventName();

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RecordTypeName();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:\\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeValue();
}

/// <summary>A domain event whose data is <typeparamref name="TData"/>, a record of identifiers, codes, numbers, flags and times.</summary>
public sealed class EventType<TData>(string name, int version, string recordType, string description, string owner)
    : EventType(name, version, recordType, description, owner, typeof(TData))
    where TData : class
{
    /// <summary>The same declaration, published until <paramref name="on"/> beside <paramref name="replacedBy"/>, which replaces it.</summary>
    public EventType<TData> Deprecated(DateOnly on, int replacedBy)
    {
        Deprecate(on, replacedBy);
        return this;
    }

    public TData Read(JsonElement data) => (TData)Deserialize(data);
}

/// <summary>
/// An event as the outbox keeps it and the dispatcher hands it on (ADR-0060): its identity and place in the outbox, what
/// it is, the record it is about, the tenant, who acted (and for whom), when, the request's correlation id, and its data.
/// </summary>
public sealed record EventEnvelope(
    Guid Id,
    long Position,
    string Type,
    int Version,
    string RecordType,
    Guid RecordId,
    string Tenant,
    Guid ActorId,
    Guid? OnBehalfOfId,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    JsonElement Data);

/// <summary>An event as a subscriber's handler receives it, its data read into its declared record.</summary>
public sealed record DomainEvent<TData>(
    Guid Id,
    string Type,
    int Version,
    string RecordType,
    Guid RecordId,
    string Tenant,
    Guid ActorId,
    Guid? OnBehalfOfId,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    TData Data)
    where TData : class;

/// <summary>
/// A subscriber's handler for one event version (TF-111, ADR-0060). It runs inside a unit of work with the record that it
/// handled the event, so what it changes and that record commit together, and a repeated delivery changes nothing. A
/// failure, as a failed result or an exception, rolls back what it did and is retried with growing waits, then parked as
/// a dead letter; the event's other subscribers go on.
/// </summary>
public interface IEventHandler<TData>
    where TData : class
{
    Task<Result> HandleAsync(DomainEvent<TData> domainEvent, CancellationToken cancellationToken);
}

/// <summary>
/// A module's subscription to an event, listed in its <c>IModule.Subscriptions</c> (TF-111, ADR-0060): its name,
/// <c>module.what_it_does</c>, which the record of what it handled keeps; the event and version it reads, from the
/// publishing module's Contracts; what it does; and its handler, which the host registers.
/// </summary>
public abstract partial class EventSubscription
{
    protected EventSubscription(string name, EventType type, string description, Type handlerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!SubscriptionName().IsMatch(name) || name.Length > MaxNameLength)
        {
            throw new ArgumentException($"'{name}' is not a subscription name: lowercase words, the module first, such as authz.end_delegations_of_deactivated.", nameof(name));
        }

        Name = name;
        Event = type;
        Description = description;
        HandlerType = handlerType;
    }

    public const int MaxNameLength = 100;

    public string Name { get; }

    /// <summary>The event and version the subscription reads.</summary>
    public EventType Event { get; }

    public string Description { get; }

    public Type HandlerType { get; }

    /// <summary>The module that subscribes: the first word of the subscription's name.</summary>
    public string Module => Name[..Name.IndexOf('.', StringComparison.Ordinal)];

    /// <summary>A subscription of <typeparamref name="THandler"/> to <paramref name="type"/>.</summary>
    public static EventSubscription To<TData, THandler>(EventType<TData> type, string name, string description)
        where TData : class
        where THandler : class, IEventHandler<TData> => new Typed<TData, THandler>(name, type, description);

    /// <summary>Reads the envelope's data into the event's record and runs the handler the scope resolves.</summary>
    public abstract Task<Result> InvokeAsync(IServiceProvider services, EventEnvelope envelope, CancellationToken cancellationToken);

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex SubscriptionName();

    private sealed class Typed<TData, THandler>(string name, EventType<TData> type, string description) : EventSubscription(name, type, description, typeof(THandler))
        where TData : class
        where THandler : class, IEventHandler<TData>
    {
        public override Task<Result> InvokeAsync(IServiceProvider services, EventEnvelope envelope, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(envelope);
            var handler = (THandler)(services.GetService(typeof(THandler)) ?? throw new InvalidOperationException($"The handler {typeof(THandler).Name} of {Name} is not registered."));
            var data = type.Read(envelope.Data);
            var domainEvent = new DomainEvent<TData>(envelope.Id, envelope.Type, envelope.Version, envelope.RecordType, envelope.RecordId, envelope.Tenant, envelope.ActorId, envelope.OnBehalfOfId, envelope.OccurredAt, envelope.CorrelationId, data);
            return handler.HandleAsync(domainEvent, cancellationToken);
        }
    }
}
