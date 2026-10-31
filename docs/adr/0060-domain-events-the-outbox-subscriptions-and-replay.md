# ADR-0060: Domain events: declared in code, an outbox in the change's transaction, subscriptions with an inbox, replay and a broker later

**Status:** Accepted
**Date:** 2026-10-31
**Decided by:** Product owner and architect, in the brief of S4 T1; accepted at the design session's review of T1 (2026-10-31), which made a removed suite's waiting deliveries held rather than dead letters (decision 10)

## Context

TF-085 asks for a transactional outbox, TF-110 for an event catalogue, TF-111 for subscriptions inside the platform,
TF-112 for inspection and replay, and TF-113 for a dispatcher a broker could replace. ADR-0042 lets an earlier module or
product hear from a later one only through events, and ADR-0001 forbids a module to call another's internals. Before T1
a module told another of a change by a call through its Contracts, or not at all. Every state change writes its audit
event in its transaction (ADR-0013); an event must be no weaker. A suite's events must exist only where the suite is
installed (TF-244, ADR-0059).

## Decision

1. **Events are declared in code, in the publishing module's Contracts.** An `EventType<TData>` names the event,
   `module.record.happened`, its version, the record type it is about, what it means and who owns it; its data is a C#
   record whose public properties are the payload's fields. A module lists its events in `IModule.Events` and its
   subscriptions in `IModule.Subscriptions`; `make catalogue` writes both into the catalogue, grouped by suite, and the
   host test that keeps the file equal to the code covers them. An event carries identifiers, not copies of records: the
   record's type and id, the tenant, the actor and the person acted for, the server's time and the correlation id, and
   the fields its declaration names. A field holds an identifier, a code (letters, digits and `_.:-`, no spaces, at most
   100), a number, a flag, a time, a day, or a list of identifiers or codes; never free text, a nested record or a name.
   A field that names a person is marked `[PersonalData]` and must be an identifier. Data that does not fit its
   declaration is refused before anything is written, as an exception: a programmer's error (TF-110 AC2).
2. **Versions.** Adding an optional field keeps the version. Any other change is a new version, declared beside the
   old, which is marked `Deprecated(on, replacedBy)`; the module publishes both until the deprecation day and until no
   subscriber reads the old one. The catalogue lists both, the old with its day (TF-110 AC3).
3. **The outbox: one kernel table, `kernel_events.outbox`, chosen by measurement.**
   `tools/measurements/outbox-place.sh` measured it on PostgreSQL 19rc1, the stack's, against an outbox table in each of
   ten module schemas: a change committed with its audit row and its event by 8 clients at once, twice each and
   alternating, and the dispatcher's poll beside a million events handed on.

   | | One kernel table | A table per module |
   |---|---|---|
   | Commits a second, 8 clients | 4,045 and 3,751 | 4,780 and 4,354 |
   | Latency of a commit | 1.98 and 2.13 ms | 1.67 and 1.84 ms |
   | The dispatcher's poll, 100 waiting | 0.057 ms | 0.542 ms |
   | The poll, idle | 0.051 ms | 0.559 ms |

   A table per module writes about 15% faster under contention, since one table has one sequence and one index; the one
   table polls ten times faster and gives every event one position, so a search, a replay's range and the hand-on read
   one place, and the retention has one table to keep. The writes' difference is 0.3 ms at about 4,000 commits a second,
   many times a unit's load: one kernel table. S9's load test reads it again (TF-228 brings partitioning).
4. **Publishing is inside the unit of work, or it throws.** `IEventPublisher.PublishAsync` writes the outbox row through
   the Events module's context, which the unit of work enlists with every other: the event commits with the change and
   its audit event, or rolls back with them. Outside a unit of work it throws: no event exists without its change, and
   no committed change loses its event. The event's id is a UUID version 7 of its own time, so an id tells when it
   happened.
5. **At least once, handled once, through an inbox.** The unit's worker runs the dispatcher. A pass first hands every
   event not yet handed on to its subscribers, writing one inbox row per subscriber (an event nobody reads is handed to
   nobody), under an advisory lock so two workers never hand one event on twice. Then it delivers what is due: the
   handler runs in a unit of work that also marks its row handled, so what the handler changed and the record that it
   ran commit together. A delivery locks its row and finds a handled one handled: a repeated delivery, by a second
   worker or a replay, changes nothing (TF-085 AC3, TF-111 AC2). A subscriber is named `module.what_it_does` and reads
   one event version through the publisher's Contracts (ADR-0042).

   The target is under a second from the event to its handling on the laptop with the stack up; TF-085 AC1's bound is
   10 seconds. An idle worker passes every 200 ms (`Events:Dispatch:IntervalMilliseconds`), and a pass that did work
   is followed at once by the next. `tools/measurements/dispatch-latency.sh` measured it on echo on 2026-10-31, after
   `make smoke` and `make e2e` on the branch's images, by the unit's own clock over every delivery handled at its first
   attempt:

   | Subscription | Deliveries | Median | 95th percentile | Longest |
   |---|---|---|---|---|
   | `authz.end_delegations_of_deactivated` | 21 | 128 ms | 225 ms | 431 ms |
   | `identity.stop_acting_for_ended_delegation` | 1 | 144 ms | 144 ms | 144 ms |
   | `samples.notice_created` | 6 | 171 ms | 322 ms | 365 ms |
   | All | 28 | 155 ms | 316 ms | 431 ms |

   Most of the median is the idle wait; the smoke's `events` step reports each run's own figure (107 ms in this one).
6. **Order per record.** A delivery is due only when no earlier event of the same record is still pending for the same
   subscriber, so two events of one record reach a subscriber in the order of their positions. A change locks its
   record's row (its version), so the order of positions is the order of commits for one record. Different records,
   and different subscribers, are delivered at once (four by default).
7. **Retries and dead letters.** A failure, a failed result or an exception, rolls back what the handler did, counts an
   attempt and waits 2, 4, 8, 16 and 32 seconds; the sixth failure parks the row as a dead letter with its error
   (`Events:Dispatch`). The dead letter is audited (`events.delivery_dead_lettered`), which the alert rule
   `event_dead_letters` raises at once per subscriber (ADR-0056). A failing subscriber holds up neither the event's other
   subscribers nor its own other records, and a dead letter holds back nothing after it (TF-085 AC2, TF-111 AC1).
8. **Inspection, replay and retention.** `events.events.read` searches events by type, record, time and correlation id,
   each with where it stands with each subscriber, the dead letters, the declared events and their subscribers.
   `events.events.replay` (high) replays one subscriber's events, named or by a range of positions, with a reason and a
   recent sign-in, audited: a dead letter goes back to pending and runs again (TF-112 AC1), an event the subscriber never
   received is given to it, and a handled one stays handled. The operator does the same for a tenant through the control
   plane, as a fleet job running the unit's `events` command. A handled event older than the tenant's retention
   (`events.retention.days`, 90 days) is archived to the tenant's container as a line of JSON, with its deliveries, and
   leaves the outbox; a search for it says it was archived (TF-112 AC2). A dead letter, a pending or a held delivery keeps its
   event: someone still owes the work.
9. **A broker later, without a change to publishers or handlers.** No broker is added. The outbox stays the source and
   the inbox the record of what each subscriber handled. A broker takes the hand-on's place: a relay reads the outbox in
   order, publishes each event to a topic keyed by its record (RabbitMQ with a consistent-hash exchange, Kafka with the
   record's id as the key, Azure Service Bus with sessions), and marks it handed on once the broker confirms; a consumer
   per subscription, in any process, runs the same handler with the same inbox check. A broker that is down leaves the
   events in the outbox; a failed publish keeps the event and raises the same alert. Publishers still call
   `IEventPublisher` and handlers still implement `IEventHandler<T>`. With no broker configured, the in-process
   dispatcher runs (TF-113 AC3). The need appears with consumers outside a unit's processes or volumes beyond one
   worker's.
10. **Suites.** A suite's modules declare its events and subscriptions; a unit runs a suite's modules only where the
    suite is installed (ADR-0059), so a unit without the suite declares, stores, hands on and replays none of its events.
    An event a removed suite's module published and nobody else reads is handed to nobody and archived in time. Each
    inbox row records the suite of its subscriber's module (none for the Foundation's). When a suite is removed, a
    delivery still waiting for one of its subscribers is **held**: a status of its own, which the console shows as "Held:
    suite removed" with the time, with no attempt counted, no error, no audit event, no alert and no dead letter, and no
    replay, since the unit holds no such subscriber. The suites a unit runs are fixed for its process, so the dispatcher
    settles them at its first pass after the update job restarts the unit: it holds the removed suites' waiting
    deliveries, and makes the held deliveries of suites the unit runs again pending at once, delivered in the order of
    their positions, by themselves. A held delivery keeps its event from the archive, since it is still owed. Held
    deliveries are removed with the suite's data when that is deleted, which a tenant's offboarding brings (TF-004, S6).
    A Foundation subscriber the unit no longer holds is a programmer's error, not a suite's removal: its deliveries still
    become dead letters naming `events.subscriber.unknown`, alerted. A subscription whose event no module of the host
    publishes is idle, and a tenant's unit has none, which a host test checks.

## Options considered

- Calls through Contracts, as before: rejected; an earlier module would have to call a later one (ADR-0042), and a
  failure of the second would fail or half-fail the first.
- An outbox table per module schema: measured, about 15% faster writes under contention, ten times slower polls, and no
  single position for search and replay; one kernel table chosen (decision 3).
- A broker now (RabbitMQ in the stack): rejected for version 1; TF-113 is a Could, the outbox and inbox would still be
  needed for the change's transaction and for handling once, and the laptop's memory is better spent elsewhere.
- Delivery records on the outbox row (a status per event): rejected; one subscriber's failure would hold the others, and
  a replay to one subscriber would touch every one.
- A schema per event in JSON Schema: rejected for now; the C# record is the schema and the catalogue writes its fields,
  which the checks at declaration and at publishing hold.

## Consequences

- Easier: a module tells others of a change by publishing, in its own transaction, and never knows who listens; a later
  product can be heard by an earlier one; a failing listener is visible, alerted and replayable.
- Harder: a handler must be idempotent in what it touches outside the unit's database (T9's webhooks keep their own
  delivery log); an event's data is a contract that changes by versions; every host that runs a publishing module runs
  the Events module, as a host that runs Identity runs Settings. The control plane runs Identity and Authorization, so
  it has an outbox of its own in its database: the fleet, its worker, delivers its events and keeps the handled ones,
  since the control plane has no files to archive them to (a later item).
- Revisit: the outbox's place with S9's load test; a broker when consumers outside a unit's processes appear; the job
  console (T2) shows dead letters beside the jobs' own.
