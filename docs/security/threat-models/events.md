# Threat model: domain events, the outbox and the dispatcher

- **Modules:** events
- **Endpoints:** none
- **Reviewed:** 2026-10-31 (S4 T1)

## What it protects

What the tenant's modules tell each other about their changes (ADR-0060): the outbox of domain events, written in the
transaction of each change; the inbox, which records what each subscriber handled, retried or parked as a dead letter;
the archive of handled events past the retention, in the tenant's container; and the replay of events to a subscriber.
An event carries identifiers, codes, numbers and times, never a copy of a record or free text, and names a person only by
identifier. A subscriber's handler changes the subscriber's own records, in the unit's database.

## Who can reach it

Modules publish through `IEventPublisher` inside their own unit of work; nothing outside the unit's processes publishes.
The worker's dispatcher runs the subscriptions the unit's modules declare. People reach the events through the API: a
signed-in person or service account with `events.events.read` searches them, sees the subscribers' states and the dead
letters; with `events.events.replay` (high) and a recent sign-in, and with a reason, replays them to a subscriber. The
operator searches and replays a tenant's events through the control plane, as a fleet job running the unit's `events`
command, with a reason in both trails.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | A request writing an event a module did not raise, or claiming another actor. | No endpoint writes events; only a module's code publishes, inside its unit of work, and the publisher takes the actor, the person acted for and the correlation id from the request or the job, never from the data. | TF-085, TF-110 |
| Tampering | An event that exists without its change, a change that loses its event, or an event's data changed before a subscriber reads it. | The event commits in the change's transaction or rolls back with it, and publishing outside a unit of work throws; data is checked against its declaration before anything is written; no endpoint changes an event; a handler and the record that it handled commit together. | TF-085 AC1, TF-110 AC2, TF-111 AC2 |
| Repudiation | Denying that a subscriber failed an event, that events were replayed, or that events were archived. | A dead letter, a replay with its reason and an archive are each audited in the tenant's trail, the operator's replay in both trails; the inbox keeps each subscriber's attempts, error and replays. | TF-112 AC1, TF-060 |
| Information disclosure | An event carrying personal data or a record's content to a subscriber, a search or an archive that should not hold it; a person without the permission reading events. | A declaration refuses free text, nested records and a person named by anything but an identifier; the search and the dead letters need `events.events.read`; the archive is the tenant's own container, which only its unit's signature reaches (ADR-0016); the operator's search answers identifiers and states, never data. | TF-110, TF-112 AC3 |
| Denial of service | A failing subscriber holding up the others, a flood of events, or a replay that runs a handler again on work it did. | Each subscriber has its own inbox row: one that fails is retried with growing waits and parked, alerted, while the others and its other records go on; the outbox and inbox are read in batches, and handled events are archived after the retention; a replay names at most 1,000 events and is idempotent through the inbox. | TF-085 AC2, TF-111 AC1 |
| Elevation of privilege | A subscriber doing what the person whose change raised the event could not, or a replay used to repeat an action. | A handler runs as the system with only what its module's rules allow, and changes only its own module's records; it never reads the event's actor as a grant. A replay needs its own high-risk permission and a recent sign-in, never while impersonating, and never runs a handler twice on an event it handled. | TF-112, TF-248 |

## What remains

- A handler that calls outside the unit (T9's webhooks and connectors) must be idempotent there too; the inbox makes it
  run once per event in the unit, not once in the world.
- The error of a failed attempt is kept in the inbox and the audit trail as the handler's message, at most 1,000
  characters: a handler that puts personal data into an exception's message puts it there. Handlers name records by id.
