# Iron.Kernel.Compliance

Electronic signatures bound to the version they sign (TF-063, TF-219), the tenant's signing policy per document type, and the periodic reviews of the audit trail, always signed (TF-219 AC3). Decisions: ADR-0051. Schema `kernel_compliance`.

## Permissions

| Permission | Risk | What it allows |
|---|---|---|
| `compliance.audit_reviews.read` | Medium | See the reviews of the audit trail and the months nobody reviewed |
| `compliance.audit_reviews.record` | High | Record and sign a review of the audit trail |

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

## A signature

A signature keeps the signer, their full name and login identifier at signing, the method (`password`, `password_and_app`, `password_and_passkey`, `password_and_recovery_code`, `company_login`, or `session` where the policy does not ask for re-authentication), the server's time, the meaning (`approved`, `rejected`, `acknowledged`, `signed`, `reviewed`), an optional comment, the record's type, id and version, and the record's snapshot at that version with its SHA-256. It never changes. Signing writes `compliance.signature_signed`; a refusal no credential could lift writes `compliance.signature_refused`; a refused attachment writes `compliance.signature_attach_refused`.

Identity proves the signer (`ISignerAuthentication`, the components rule of Part 11 section 11.200): the first signature of a session asks for the login identifier, the password and the second factor the person has; later ones for the password, and the second factor when `compliance.signing.later_second_factor` is on; a person without a password signs after a fresh sign-in at their company login. Nobody signs while impersonating, while acting for someone, with a token, as a service account or as a break-glass account.

The snapshot's canonical form is in the Audit README, beside the audit event's. A module signs inside its own action: the developer guide's "Signing a record" says how.

## Policy

`compliance.signing.policies` (tenant) is an object keyed by record type, each with `required`, `meanings` and `reauthenticate`; a type it does not name keeps its module's default. The audit-trail review is always signed with re-authentication, whatever it says.

## Model change

Edit the entity, then `make ef-add MODULE=Compliance NAME=<WhatChanged>` and commit `Data/Migrations`.
