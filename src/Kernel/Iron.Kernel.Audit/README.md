# Iron.Kernel.Audit

Kernel module (ADR-0029). Schema `kernel_audit`. The audit trail (TF-060, ADR-0013), sealed into a chain whose anchors live outside the tenant's database, searchable and exportable for its whole retention, archived to the tenant's container, with the platform's retention classes and legal holds (TF-061, TF-064, TF-066, ADR-0050).

| Folder | Holds |
|---|---|
| `Contracts/` | `AuditEvent`, `AuditOutcome`, `AuditSource`, `AuditSeverity`, `AuditPermissions`, `AuditEvents`, `AuditErrors`, `IAuditWriter`, `IAuditTrail` and `AuditTrailEntry`; the chain's `ChainedEvent`, `AuditCanonicalForm`, `ChainHead`, `AuditAnchor`; the verification's `AuditChainCheck`, `SealedEvent`, `ChainProblem`; the archive's `AuditArchiveFormat` |
| `Data/` | `AuditDbContext`; `AuditEventRecord` (append-only, then sealed once); `AuditSeal`; `AuditArchiveBatch`, `AuditPrune`, `LegalHold`, `RetentionChoice`, `AuditExport`; the design-time factory, generated `Migrations/` with the chain's guards |
| `Services/` | `AuditWriter`, `AuditTrail`; `AuditSealer` and its worker; `AuditAnchorSender` and `ControlPlaneClient`; `AuditArchiver`, `AuditPruner`, `AuditArchiveSearch`, `AuditExporter` and the daily `AuditRetentionWorker`; `RetentionPolicy`, `LegalHolds`, `BlobRecordArchive`, `RetentionAuditWriter` |
| `Api/` | the trail's search, export, archive search and chain status; retention classes; legal holds |

## Permissions

| Permission | Risk | Lets a person |
|---|---|---|
| `audit.events.read` | Medium | search the trail and the archive, export it, read the chain's status, the retention classes and the holds |
| `audit.retention.manage` | High | choose a class's period within the operator's limits, with a reason and step-up |
| `audit.legal_holds.manage` | Critical | place and release legal holds, each with a reason and step-up |

## Endpoints

| Method and path | What |
|---|---|
| `GET /api/v1/audit-events` | The list grammar over the database's events: filter by `created_at`, `event_type`, `actor_id`, `on_behalf_of_id`, `impersonator_id`, `record_type`, `record_id`, `source`, `severity`, `outcome`, `correlation_id`, `chain_position`. A search by record in time order is TF-064 AC1. |
| `POST /api/v1/audit-events/exports` | The same query to CSV in the tenant's container, its first lines the title and the filters, every cell guarded against spreadsheet formulas; answers its download address. Audited as `audit.events_exported`. |
| `GET /api/v1/audit-events/exports/{id}/content` | The CSV. |
| `GET /api/v1/audit-events/archived` | The archive by date range (`from`, `to`) and `event_type`, `actor_id`, `impersonator_id`, `record_type`, `record_id`, `source`; at most 500 rows, oldest first (TF-064 AC2). |
| `GET /api/v1/audit-chain` | The chain's head, what waits for a seal, archived and pruned positions, the latest seals, how far the control plane holds anchors, and its latest verification. |
| `GET /api/v1/retention`, `PUT /api/v1/retention/{code}` | The classes with their days and the operator's limits; a choice with `days` and `reason`, refused below the minimum (TF-066 AC3). |
| `GET /api/v1/legal-holds`, `POST /api/v1/legal-holds`, `POST /api/v1/legal-holds/{id}/release` | Holds on the tenant, a record or a master's records, each placed and released with a reason. |

## The writer

Both people (TF-037, ADR-0037): while a person acts for a delegator, the writer fills `on_behalf_of_id` with the delegator from the request, so every event names both without the module saying so; the list answers `actor_name` and `on_behalf_of_name` when Identity is in the host.

Impersonation (TF-025, ADR-0049): while someone is signed in as another person, the writer fills `impersonator_id` from the request and adds `details.impersonator` with their name. An operator is nobody in the unit: their id is the control plane's, and the name in the details stands for them.

Severity (ADR-0049): `normal` or `high`; `high` marks what an administrator must look at. `IAuditTrail.ActionsOfAsync` answers a principal's events in order, for break-glass's review.

Event types are named by the module that raises them: `authz.permission_denied`, `identity.login_failed`.

## The chain (ADR-0050)

Events are written as before, by many transactions at once. The unit's worker seals them every few seconds (`Audit:Sealing:Seconds`, 5): under the chain's lock, oldest first, each takes the next position, the hash before it and its own hash, through `kernel_audit.seal_event`, the one way the chain columns are set, once, at the position after the head. A trigger refuses any change to a sealed event and any change but the chain columns to an unsealed one, whatever the role. Positions are consecutive, so a gap always means a deleted event.

After each seal the worker sends the control plane an anchor (position, hash, time) as the unit's own client (`ControlPlane:ClientId` and `ClientSecret` in the unit secret, made by the fleet). The fleet verifies the chain daily against those anchors with a reader role of its own (`<tenant>_audit`, `IVerifierReadable` tables only); the unit never verifies itself.

The app role has SELECT and INSERT on `kernel_audit.events`, and EXECUTE on the schema's functions; no UPDATE and no DELETE (TF-060 AC2).

### The canonical form, version 1, for auditors

Every sealed event keeps `hash_version` (1), `previous_hash` and `hash`. To recompute `hash`:

1. Write a JSON array, compactly (no spaces), of these 19 values in this order:
   1. the text `"iron-audit-v1"`;
   2. `id`;
   3. `chain_position`, a whole number;
   4. `created_at` in UTC as `yyyy-MM-ddTHH:mm:ss.ffffffZ` (six decimals, the database keeps microseconds);
   5. `created_by`;
   6. `event_type`;
   7. `outcome`;
   8. `actor_id`;
   9. `on_behalf_of_id`;
   10. `impersonator_id`;
   11. `source`;
   12. `severity`;
   13. `target`;
   14. `record_type`;
   15. `record_id`;
   16. `reason`;
   17. `correlation_id`;
   18. `details`;
   19. `previous_hash`.
2. Identifiers are lowercase with hyphens (`01a14e12-f3a8-7155-b5b6-17c3abceae77`). A missing value is `null`.
3. Text is Unicode normalisation form C. Only `"` and `\` and the control characters below U+0020 are escaped: `\b \f \n \r \t`, the rest as `\u00xx` in lowercase. Every other character is written as itself.
4. `details` is a JSON object with its keys in ordinal order (by UTF-16 code unit; for the keys the platform writes, ASCII order), at every depth; strings follow rule 3; a number is its decimal value without exponent, without trailing zeros after the point, and without a point when whole.
5. The hash is SHA-256 of the array's UTF-8 bytes, in lowercase hexadecimal. The first event's `previous_hash` is 64 zeros; every other event's is the hash of the event at the position before it.

An archived batch (`audit/archive/{first}-{last}.jsonl` in the tenant's container) holds one event per line with these fields under the same names, and the chain's. This Python, standard library only, recomputes every hash of a batch and checks the links:

```python
import hashlib, json, sys, unicodedata
from decimal import Decimal

ESCAPES = {'"': '\\"', "\\": "\\\\", "\b": "\\b", "\f": "\\f", "\n": "\\n", "\r": "\\r", "\t": "\\t"}

def text(value):
    if value is None:
        return "null"
    out = []
    for ch in unicodedata.normalize("NFC", value):
        out.append(ESCAPES.get(ch) or ("\\u%04x" % ord(ch) if ord(ch) < 0x20 else ch))
    return '"' + "".join(out) + '"'

def value(v):
    if v is None:
        return "null"
    if v is True or v is False:
        return "true" if v else "false"
    if isinstance(v, Decimal):
        s = format(v.normalize(), "f")
        return "0" if s == "-0" else s
    if isinstance(v, str):
        return text(v)
    if isinstance(v, list):
        return "[" + ",".join(value(x) for x in v) + "]"
    return "{" + ",".join(text(k) + ":" + value(v[k]) for k in sorted(v, key=lambda k: k.encode("utf-16-be"))) + "}"

def canonical(e, previous):
    fields = ["id", "position", "created_at", "created_by", "event_type", "outcome", "actor_id", "on_behalf_of_id",
              "impersonator_id", "source", "severity", "target", "record_type", "record_id", "reason", "correlation_id"]
    parts = [text("iron-audit-v1")]
    for name in fields:
        parts.append(str(e[name]) if name == "position" else text(e[name]))
    parts += [value(e["details"]), text(previous)]
    return "[" + ",".join(parts) + "]"

previous = None
for line in open(sys.argv[1], encoding="utf-8"):
    e = json.loads(line, parse_float=Decimal, parse_int=Decimal)
    e["position"] = int(e["position"])
    if previous is not None and e["previous_hash"] != previous:
        print(f"position {e['position']}: links to {e['previous_hash']}, not {previous}")
    digest = hashlib.sha256(canonical(e, e["previous_hash"]).encode("utf-8")).hexdigest()
    print(f"position {e['position']}: {'ok' if digest == e['hash'] else 'CHANGED'}")
    previous = e["hash"]
```

For events still in the database, the same columns come from `kernel_audit.events` (`chain_position` is the position; `created_at` written as rule 4). A test recomputes the form with an implementation of its own (`AuditChainTests`).

## Archive, pruning and retention (ADR-0050)

The daily `AuditRetentionWorker`, in the unit's worker:

- **archives** sealed events older than the class `audit_online` (13 months by default) to the tenant's container in batches that keep every chain field, indexed in `archive_batches` by position and time, then removes them from the database through `kernel_audit.remove_archived`, which deletes archived rows only, from the oldest end;
- **prunes** whole archived batches older than the class `audit_event` (7 years by default, never below the operator's minimum), from the oldest end only, writes `AuditPrune` with the position and hash it reached, and the unit anchors it as `pruned`, so the rest still verifies; a hold on the whole tenant stops it;
- **runs the record retention** (`RecordRetentionRunner` in `Iron.Kernel.Data`): for every record type a module put into a class (`IRetainedRecords`), it archives each record past its period to `retention/{type}/{yyyy-MM}/{id}.json`, has the module delete it, and writes `audit.record_expired`, skipping any record a legal hold keeps (the record, its master, or the whole tenant).

The operator's limits per class are configuration (`Retention:Limits:<class>:MinimumDays` and `MaximumDays`); the tenant's choices are `RetentionChoice` rows, each with its reason, audited as `audit.retention_changed`; holds as `audit.legal_hold_placed` and `audit.legal_hold_released`, the archive and pruning as `audit.trail_archived` and `audit.trail_pruned`.

## Model change

Edit the entity, then `make ef-add MODULE=Audit NAME=<WhatChanged>` and commit `Data/Migrations`. A change to the canonical form is a new version: keep version 1's code, so every hash made with it still verifies.
