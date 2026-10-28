# Threat model: the audit chain

- **Modules:** audit
- **Endpoints:** none
- **Reviewed:** 2026-10-28 (S3 T10a)

## What it protects

The tenant's audit trail: that every state change has its event, that no event is changed or removed unseen, and that
field history and signature evidence keep their meaning. Regulated tenants rely on it (21 CFR Part 11, EU Annex 11).

## Who can reach it

The unit's own code, which writes each event in the transaction of its change; the unit's worker, which seals events
into the chain; people with the audit permissions, who search, export and review; the fleet's reader role, which
verifies; the control plane, which keeps the anchors.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | An event written in another person's name. | The server writes the actor from the session, never from the request; an impersonation names both people. | TF-060, TF-025 |
| Tampering | Changing or deleting an event in the database. | An append-only table guarded by a trigger and one sealing function; a SHA-256 chain over a canonical form; each seal anchored in the control plane; a daily verification with a role of its own, in full monthly. | TF-061 |
| Tampering | Restoring the database to hide events. | The verification names the restore point; an operator accepts it with a ticket, and the chain continues as a new segment that shows the range lost. | TF-061 |
| Repudiation | A change without its event. | The event is written in the same unit of work as the change; times come from the server's clock. | TF-060, TF-065 |
| Information disclosure | Reading the trail without a right to it. | Search and export need their permissions; exports at most 20 an hour per person and audited; logs mask personal data and never reach the audit store. | TF-064, TF-069, TF-072 |
| Denial of service | Filling the trail with refused calls. | The API's limit per principal; a refusal of the rate limit is audited once per window. | TF-072 |
| Elevation of privilege | The database owner rewriting the chain. | The anchors live outside the unit's database, so a rewrite fails the next verification. | TF-061 |

## What remains

- In Azure the anchors also go to immutable storage, the first Azure deployment's requirement (ADR-0050).
- Alerts on a broken chain with their thresholds are T10b's (TF-079); today the fleet's verification records the
  break and logs the alert.
