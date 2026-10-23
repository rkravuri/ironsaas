# Iron.Kernel.Compliance

Electronic signatures bound to the version they sign (TF-063, TF-219), the tenant's signing policy per document type, the periodic reviews of the audit trail, always signed (TF-219 AC3), the evidence packs an auditor asks for (TF-067), and the validation pack of the version the unit runs (TF-218). Decisions: ADR-0051, ADR-0052. Schema `kernel_compliance`.

## Permissions

| Permission | Risk | What it allows |
|---|---|---|
| `compliance.audit_reviews.read` | Medium | See the reviews of the audit trail and the months nobody reviewed |
| `compliance.audit_reviews.record` | High | Record and sign a review of the audit trail |
| `compliance.evidence.export` | High | Make and download evidence packs: the whole tenant's access, actions and settings for a period |
| `compliance.validation.read` | Medium | See the installed version and download its validation pack |

A record's signatures are read by whoever may read the record, as the record's module says (`ISignableRecords.MayReadAsync`).

## Endpoints

| Method and path | Needs | What it does |
|---|---|---|
| `GET /api/v1/signatures/types` | signed in | The document types that can be signed, with the tenant's policy and the default |
| `GET /api/v1/signatures/challenge?record_type=` | signed in | What your next signature asks for |
| `GET /api/v1/signatures?record_type=&record_id=` | the record's read | A record's signatures, each with its hash checked and whether the record changed since |
| `GET /api/v1/signatures/{id}/snapshot` | the record's read | The signed snapshot, its canonical text and its hash recomputed |
| `POST /api/v1/signatures/{id}/attach` | the record's read | Apply a signature to a record; refused and audited unless the hashes match (TF-219 AC2) |
| `GET /api/v1/audit-reviews` | `compliance.audit_reviews.read` | The reviews, newest period first |
| `GET /api/v1/audit-reviews/coverage?months=` | `compliance.audit_reviews.read` | The last months with the reviews that cover each |
| `GET /api/v1/audit-reviews/{id}` | `compliance.audit_reviews.read` | One review with its signature |
| `POST /api/v1/audit-reviews` | `compliance.audit_reviews.record` | Record a review and sign it "reviewed" |
| `POST /api/v1/evidence-packs` | `compliance.evidence.export` | Ask for a pack for a period (`from`, `to`, instants in UTC); answers 202 with the pack, requested |
| `GET /api/v1/evidence-packs`, `GET /api/v1/evidence-packs/{id}` | `compliance.evidence.export` | The packs, newest first; one pack with its state, hashes and the chain head it names |
| `POST /api/v1/evidence-packs/{id}/links` | `compliance.evidence.export` | A link to download a ready pack: for you, once, within five minutes |
| `GET /api/v1/evidence-packs/downloads/{token}` | `compliance.evidence.export` | The pack's ZIP through its link, its bytes checked against its hash first |
| `GET /api/v1/validation` | `compliance.validation.read` | The installed version, whether it is a development build, and whether its pack is here |
| `GET /api/v1/validation/pack` | `compliance.validation.read` | The validation pack of the installed version |

## A signature

A signature keeps the signer, their full name and login identifier at signing, the method (`password`, `password_and_app`, `password_and_passkey`, `password_and_recovery_code`, `company_login`, or `session` where the policy does not ask for re-authentication), the server's time, the meaning (`approved`, `rejected`, `acknowledged`, `signed`, `reviewed`), an optional comment, the record's type, id and version, and the record's snapshot at that version with its SHA-256. It never changes. Signing writes `compliance.signature_signed`; a refusal no credential could lift writes `compliance.signature_refused`; a refused attachment writes `compliance.signature_attach_refused`.

Identity proves the signer (`ISignerAuthentication`, the components rule of Part 11 section 11.200): the first signature of a session asks for the login identifier, the password and the second factor the person has; later ones for the password, and the second factor when `compliance.signing.later_second_factor` is on; a person without a password signs after a fresh sign-in at their company login. Nobody signs while impersonating, while acting for someone, with a token, as a service account or as a break-glass account.

The snapshot's canonical form is in the Audit README, beside the audit event's. A module signs inside its own action: the developer guide's "Signing a record" says how.

## Policy

`compliance.signing.policies` (tenant) is an object keyed by record type, each with `required`, `meanings` and `reauthenticate`; a type it does not name keeps its module's default. The audit-trail review is always signed with re-authentication, whatever it says. The signing settings belong to the four-eyes area `signing_policy` (ADR-0052).

## Evidence packs (TF-067, ADR-0052)

A request (`compliance.evidence_pack_requested`, actor the requester) records the period; a period that ends later than now ends now, and a period is at most ten years. The unit's worker (`EvidencePackWorker`, every five seconds) makes the next pack as the system, on the requester's behalf (`compliance.evidence_pack_made` or `compliance.evidence_pack_failed`), into the tenant's container at `evidence/<id>.zip`.

Each module contributes its tables through `IEvidenceSource` (`Contracts/Evidence.cs`), which `EvidencePacks` gathers by document, in this order and folder:

| Folder | Document | From |
|---|---|---|
| `1-access_list` | Access list | Identity: people and service accounts, company logins without their secrets, emergency accounts, documents in force. Authorization: roles and scopes at the period's end, grants and removals in it with who made them, roles with their permissions, conflict rules |
| `2-administrative_actions` | Administrative actions | Audit: every event of a type declared administrative (`AuditEventType.AsAdministrative()`), with actor, impersonator, acting for, target, record, reason and chain position |
| `3-security_settings` | Security settings | Settings: password and lockout, MFA, sessions, impersonation, signing and four-eyes, at the period's end. Audit: retention classes, legal holds standing |
| `4-chain_verification` | Audit chain verification | Audit: the control plane's verifications of the period, and the restores operators accepted |
| `5-backup_drills` | Backup and restore drills | Audit: the drills; until the first Azure deployment brings backups, one sentence says so, with the accepted restores |

"At the period's end" is rebuilt: a tracked record's state now with every save the field history recorded since undone (`IRecordHistory`, `RecordStates.Rewind`, `TrackedFields`). A tenant made before T7 has kept field history only since T7 reached it (`IRecordHistory.KeptSinceAsync`: the first event sealed in canonical form version 2, when older events exist): when a period starts before that, the access list says so at its top, on its page and in each of its CSVs, with the time from which it is reliable, and the manifest names that time as `field_history_since`. Each document is a page (`<document>.html`) and one CSV per table, each headed by the tenant and the period, CSV cells guarded against spreadsheet formulas. `manifest.json` at the root (format `iron-evidence-pack-v1`) lists every file with its SHA-256 and size, the tenant, the period, the requester by id and name, when it was asked for and made, and the audit chain's head: the latest anchored seal's segment, position and hash with the control plane's anchor (`IEvidenceChain`, Audit's `AuditEvidence`). The ZIP's entries are in path order with a fixed time, so the same files make the same bytes.

To check a pack: unzip it, recompute each file's SHA-256 against the manifest, and compare the head with the anchor the control plane holds for that position and with the sealed event at that position in the tenant's trail (the canonical form is in the Audit README).

A download takes a link (`EvidenceDownloadLink`): for the person who asked for it, once, within five minutes; the bytes are checked against the pack's hash before they leave, and `compliance.evidence_pack_downloaded` names the downloader with the requester in its details.

## The validation pack (TF-218, ADR-0052)

The unit knows its version from `Release:Version`, which the release workflow stamps into the image (`Release__Version`); an image built on a laptop says `development`. `/me/context` answers it as `release` for the shell's help panel. At each provisioning and update the fleet places the pack of exactly that version in the tenant's container at `validation/<version>/validation-pack-<version>.zip` (the Fleet README); `ValidationPacks` reads it there, without asking the control plane, and audits each download as `compliance.validation_pack_downloaded`. A missing pack answers 404 with `compliance.validation.pack_missing`, and the page says it is placed at the next update.

## Model change

Edit the entity, then `make ef-add MODULE=Compliance NAME=<WhatChanged>` and commit `Data/Migrations`.
