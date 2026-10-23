# ADR-0050: The audit chain, its verification, archive and retention, legal holds, the time source and logs apart

**Status:** Accepted
**Date:** 2026-10-23
**Decided by:** Product owner and architect, at the design session's review of S3 T6 (2026-10-23), which added the accepted restore and its segments, and the operators and break-glass accounts in the search

## Context

The audit trail has been append-only since S1 (TF-060, ADR-0013): the app role may insert and read audit rows, never change or delete them. That stops the application, not someone who gets past the database's permissions. S3 T6 builds five requirements on it: the integrity chain (TF-061), search, export and archive (TF-064), the time source (TF-065), retention with legal holds (TF-066), and operational logs kept apart from the trail (TF-069). It rests on the unit and the fleet of ADR-0016 to ADR-0018, the settings of ADR-0036, the step-up of ADR-0047, and the traceability of ADR-0044.

## Decision

### The chain: sealed by a job, not at insert

- **Each audit event gains** its chain position (consecutive per tenant from 1), the previous event's hash, its own hash, the canonical form's version and when it was sealed. They are null until the event is sealed.
- **Events are written as before**, by many transactions at once. The unit's worker seals them a few seconds later (`Audit:Sealing:Seconds`, 5): one pass takes the chain's lock (`pg_advisory_xact_lock`, one sealer per tenant database whichever process runs it), reads the committed unsealed events oldest first (their time, then their id), at most 500, and gives each the next position, the hash before it and its own hash.
- **Positions come from the chain's head, not a sequence.** A sequence leaves gaps when a transaction rolls back, and the verification would report each gap as a deleted event. Sealing gives consecutive positions, so a gap always means a deleted event.
- **Why not at insert:** chaining each event inside its own transaction would serialise every write of the tenant on the chain's head. The seal costs a few seconds of delay before an event has its place; an event waiting for a seal is listed as "Not sealed yet".
- **The database enforces it.** The app role has no UPDATE on the events. `kernel_audit.seal_event`, owned by the schema's owner, is the one way to set the chain columns: once, and only at the position right after the head. A trigger refuses any change to a sealed event, and any change to an unsealed one other than its chain columns, for every role while it stands, the owner's included. TF-060 AC2 still holds.

### The canonical form, version 1

- The hash is SHA-256, lowercase hex, over the UTF-8 bytes of a JSON array of the event's fields in a fixed order: the label `"iron-audit-v1"`, then id, position, time, created by, event type, outcome, actor, on behalf of, impersonator, source, severity, target, record type, record id, reason, correlation id, details, previous hash.
- Times are UTC as `yyyy-MM-ddTHH:mm:ss.ffffffZ`. Ids are lowercase with hyphens. Text is Unicode NFC, and only `"`, `\` and control characters are escaped (`\b \f \n \r \t`, the rest as `\u00xx`). A missing value is `null`. Details are an object with keys in ordinal order. The first event's previous hash is 64 zeros.
- The form is versioned (`hash_version`): a change of form is a new version, and a verifier refuses a version it does not know. The Audit module's README writes it out for auditors, with a recomputation in a few lines of Python; a test recomputes it with an independent implementation.

### Anchors outside the tenant's database

- After each seal, the unit sends an anchor (position, hash, time) to the control plane, as the unit's own client: the fleet makes one per unit (client credentials, the `unit-client` role, which carries only `tenancy.audit_anchors.append`) and puts its secret in the unit secret. The control plane binds the client to its tenant, so a unit can only anchor its own chain.
- The control plane keeps anchors append-only: the same anchor again is answered as taken; another hash for an anchored position is refused (409), audited at high severity and counted (`audit.anchor_conflicts`). Its tables take inserts only (a test proves the grants refuse updates and deletes).
- **In Azure, anchors also go to immutable blob storage** (time-based retention). The laptop's storage emulator cannot enforce immutability, so this is the first Azure deployment's requirement.
- On the laptop the control plane connects as its database's owner; the insert-only grants are proven in tests, and a control-plane role of its own is T9's (the review set it there).

### The verifier is not the writer

- **The fleet verifies, not the unit.** Each tenant's database gains a verifier role (`<tenant>_audit`) that can read the chain's tables and nothing else; its password is in the fleet's part of the store, never in the unit secret. A unit that had been taken over cannot fake its own verification.
- **Daily** the fleet checks what was sealed since the last verification that passed; **monthly**, after a failure, or when an operator asks (`tenancy.audit_chains.verify`, `make verify-audit`), it checks from the oldest position still kept. It reads the archived part from the tenant's container with its own account.
- Every event must follow at the next position, link to the hash before it, and recompute to its own hash; every anchor in the range must match. A missing position is a deleted event, a recomputation that differs is a changed event, each reported with its position and the event's identifier (TF-061 AC2).
- **A record of every run** goes to the control plane: the range, the result, what it found, the time (AC1). A run that did not pass raises the alert: an error line starting `ALERT:`, the counter `audit.verification_failures`, and a high-severity event in the control plane's trail.
- **After a restore** the chain ends before its latest anchor. The result is `restored`, not `failed`, when that is the only disagreement, and the record names the restore point: the last position whose hash matches an anchor, with the anchor's time (AC3). Anything else, a missing or changed event above all, is `failed`.

### An accepted restore, and the chain's segments

- **An operator accepts a restore point** the latest verification names, with a ticket, a reason and a fresh token (`tenancy.audit_restores.accept`, critical; `make audit-accept-restore TENANT=x TICKET=y REASON="..."`). Only a `restored` result can be accepted, and only once. The control plane keeps the record (the restore point, its hash and time, the range lost, the ticket, the reason, the operator) for good, writes `tenancy.audit_restore_accepted` at high severity, and asks the fleet for a full verification.
- **The chain continues as a new segment.** The first segment starts at position 1 from the genesis hash; each accepted restore starts the next from the position after the restore point, its first event linking to the restore point's hash. A position belongs to the latest segment that starts at or before it.
- **Anchors are keyed by segment and position.** An anchor goes to the segment that covers its position when it arrives. A position the restore lost keeps its old anchor in the segment before, as the record of what was lost, and the new segment anchors that position afresh without a conflict.
- **The verification covers every segment and the links between them.** An anchor counts only in the segment that covers its position, so the lost range is no disagreement; the first event of each segment must link to the restore point the operator accepted, or the result is `failed` (`relinked`). After an accepted restore, the verification passes.
- **The tenant's own trail records it.** The unit asks the control plane where it stands once a minute: a restore it has not recorded goes into its `chain_restores` table with the event `audit.restore_accepted`, in one transaction, and it sends its anchors again from the restore point in the new segment. The Chain status page always shows every accepted restore with the range it lost, who accepted it, the ticket and the reason.
- A tenant the fleet cannot reach is tried again an hour later.

### Search, export and the History tab

- `audit.events.read` searches by time, actor, impersonator, record, type and source; a search by record answers its events in time order (TF-064 AC1); without the permission it is refused (AC3).
- **"Signed in as them by"** offers, by name and grouped, everyone the trail names as having signed in as someone (the platform's operators, by the name the writer kept with each event, and the tenant's people) and every break-glass account. A break-glass account acts as itself, so choosing one finds its events as the actor (ADR-0049); the results name them all.
- **An export** writes the search to CSV in the tenant's container (`audit/exports/{id}.csv`), its first lines the title and the filters, every cell guarded against spreadsheet formulas, and hands it over as a download. The export is itself an audit event.
- The record template's History tab shows a record's events, newest first, to a person who may read the trail.

### The archive keeps the chain; pruning keeps it verifiable

- **The online period** is a retention class, `audit_online`, 13 months by default. Older sealed events move to the tenant's container in batches of JSON lines (`audit/archive/{first}-{last}.jsonl`) that keep every chain field, indexed by position and time in the database; then they leave the database, through a function that deletes only archived rows from the oldest end. Search by date range reads the index and the batches (TF-064 AC2, at most 500 rows an answer), and verification reads them with the database's part as one chain.
- **Audit retention** is the class `audit_event`, 7 years by default, never below the operator's minimum. Past it, whole archived batches are removed from the oldest end only, and the unit anchors "pruned up to position N with hash H"; the rest still verifies from there. A hold on the whole tenant stops it.
- Both are chosen on the retention page, not in the settings module: they are retention classes with the operator's limits, like every other.

### Retention classes and legal holds

- **Each module puts each record type it owns into a class** (`IRetainedRecords`): business documents (7 years from closure), sessions (12 months from their end), security events, notifications, the audit trail and its online period. The operator sets each class's limits (`Retention:Limits:<class>`); a tenant chooses within them with `audit.retention.manage`, a reason and step-up; a value outside them is refused (TF-066 AC3).
- **The retention job** (daily, in the unit's worker) finds the records past their class's period, writes an archive copy to the tenant's container (`retention/{type}/{yyyy-MM}/{id}.json`), has the owning module delete them, and writes `audit.record_expired` for each (AC1).
- **A legal hold** names one record, every record of a master (a supplier for its documents), or the whole tenant. A held record is skipped (AC2). Placing one needs `audit.legal_holds.manage` (critical), a reason and step-up; releasing one needs a reason and step-up too, and a released hold stays listed. Both are audited.
- The kernel's own sessions are the first real record type; the data tests' sample module proves the rest.

### The time source

- **Every stored time is the server's.** The unit of work stamps each record's times from `TimeProvider` and overwrites any value a caller set (TF-065 AC1). No request body carries a time the server records: a test walks both OpenAPI documents and allows only periods a person chooses (a delegation's dates, a review's due day, a plan's term) and an anchor's time, which is the unit's server clock, kept beside the control plane's own receipt time.
- **Each process measures its clock** against a time server by SNTP every ten minutes (`Telemetry:Clock`, `pool.ntp.org` by default). An offset above one second raises the alert: an `ALERT:` error line at most once an hour, the gauge `clock.offset_seconds`, and the readiness check `clock` turned Degraded (AC3). A time server it cannot reach is a warning: the clock may be right. In Azure the server is the platform's own.
- **The shell shows every time in the person's zone, with the zone named** ("Oct 22, 2026, 2:30 PM GMT+5:30", AC2). The zone is the person's setting `shell.time_zone`, chosen on My profile, the browser's when they chose none. A calendar day with no time of its own is shown as that day in no zone. A searched day starts and ends at midnight in the person's zone, so a day the clocks change is 23 or 25 hours.

### Operational logs apart from the audit trail

- **Logs carry identifiers, not people** (TF-069 AC1, AC3). The scrubbing rule of ADR-0046 masks, with `[masked]`, any text shaped like an email address, and the whole value of a property whose last word is a name, an email, a mail, a phone, a mobile, an address, a street, a postcode or a birthday, in its value and where the message shows it. On spans it masks the person's own attributes (`enduser.*`, `user.*`); `server.address` is not a person.
- **Logs are never the only record of an action** (AC2): the action's audit event is written in its transaction (ADR-0013); Loki deletes log lines after 30 days, and the audit event stays for its retention.
- **No log tool can reach the audit store** (AC4): Grafana's data sources and the collector's exporters are Prometheus, Loki and Tempo only, none a database; the telemetry code references neither the audit module nor the data layer. Deleting or changing audit events through the log tools is impossible because they hold none.

## Options considered

- Chain at insert, inside each transaction: simple, but serialises every write of a tenant on one row. Not chosen.
- A database sequence for positions: no lock, but gaps on rollback look like deletions. Not chosen.
- The unit verifies its own chain: no new role, but a taken-over unit could report a pass. Not chosen.
- Anchors in the tenant's own database or container: no control-plane call, but whoever changes the trail changes the anchors with it. Not chosen.
- Pruning event by event: finer, but every gap would need its own anchor; whole archived batches from the oldest end keep one "pruned up to" anchor. Chosen.
- The online period and audit retention as settings (ADR-0036): they need the operator's per-class limits, which retention classes have. Retention classes chosen.
- Masking logs by an allow-list of properties: safer in principle, but every module's log lines would need it; masking by the property's last word and the email shape catches what slipped past without it. Chosen, with identifiers as the rule.

## Consequences

- Tampering with the trail is now detectable by someone outside the unit, and the trail is searchable for its whole retention, in the database and the archive alike.
- An event waits a few seconds for its place in the chain; a page may show "Not sealed yet".
- Units made before T6 have no verifier role and no unit client: `make update-unit` gives them both. Until then the fleet logs once an hour that it cannot verify them, and nothing is anchored. Units reach the control plane at `http://control-api:8080` when it runs in containers (`make up-control`), not from `make run-control`.
- After a restore and before its acceptance, the unit's anchors for positions the control plane anchored before are refused as conflicts, and each verification reports `restored` with the same restore point; that is the state an operator investigates and then accepts.
- T9 seals the control plane's own audit trail into a chain and gives the control plane its own database role rather than the owner's (the review set both there); its anchors, restores and verification records are append-only now.
- Later: immutable blob storage for anchors (the first Azure deployment); the operator console's pages for verifications and restores (the records, alerts and make targets exist now).
