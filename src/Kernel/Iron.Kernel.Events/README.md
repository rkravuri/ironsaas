# Iron.Kernel.Events

**Layer: Kernel.** Domain events have no business meaning of their own: a module's event is the module's, and this
module only carries it (ADR-0029). Schema `kernel_events`.

Domain events, the transactional outbox, subscriptions with an inbox, the dispatcher, inspection, replay and the archive
(TF-085, TF-110 to TF-113, ADR-0060).

- **Declared by modules.** A module declares each event it publishes in its Contracts, as an `EventType<TData>` with its
  name, version, record type, data, description and owner, and lists them in `IModule.Events`; it lists the events it
  reads, each with its handler, in `IModule.Subscriptions`. `make catalogue` writes both into the catalogue by suite.
- **The outbox.** `IEventPublisher` (this module's Contracts) writes an event into `kernel_events.outbox`, only inside
  the unit of work of its change: the event commits with the change and its audit event, or not at all. Data is checked
  against its declaration first. One kernel table, chosen by measurement (`tools/measurements/outbox-place.sh`).
- **The dispatcher**, in the unit's worker (`AddEventDispatch`): it hands each event to its subscribers, one row each in
  `kernel_events.inbox`, then delivers what is due, in order per record and subscriber, different records at once. A
  handler runs in a unit of work that marks its row handled, so a repeated delivery changes nothing. A failure is retried
  after 2, 4, 8, 16 and 32 seconds, then parked as a dead letter, audited (`events.delivery_dead_lettered`) and alerted
  (`event_dead_letters`). `Events:Dispatch` sets the interval (200 ms when idle), the batch, the parallelism and the retries.
- **Inspection and replay.** `GET /api/v1/events` (by type, version, record, actor, time and correlation id),
  `/events/{id}`, `/events/dead-letters`, `/events/types` and `/events/retention` need `events.events.read`;
  `POST /api/v1/events/replay` needs `events.events.replay`, a recent sign-in and a reason, and is audited. A replay is
  idempotent: a handled event stays handled. The console's Events page uses them; the operator reaches the same through
  the control plane (`make events`, `make events-replay`), which runs the unit's `events` command.
- **The archive.** A handled event older than `events.retention.days` (90 by default) goes to the tenant's container
  under `events/archive/`, a line of JSON each with its deliveries, and leaves the outbox and the inbox, a thousand at a
  time, audited (`events.events_archived`). A dead letter or a pending delivery keeps its event.

Every host that runs a module that publishes runs this module too, as the unit's hosts do (`UnitModules`).
