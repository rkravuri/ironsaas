# ADR-0051: Change history in the audit chain, electronic signatures bound to their version, and identifiers never reused

**Status:** Proposed
**Date:** 2026-10-23
**Decided by:** Product owner and architect, in the brief of S3 T7; written by the build session, for the design session's review

## Context

S3 T7 builds the core of 21 CFR Part 11 and EU Annex 11 on the audit trail T6 sealed into a chain (ADR-0050): field-level change history for every business record (TF-062), electronic signatures bound to the exact version they sign (TF-063), their manifestation on screen and on paper, and periodic reviews of the audit trail (TF-219). It rests on step-up and second factors (ADR-0047), company logins (ADR-0048), impersonation and acting for someone (ADR-0037, ADR-0049), settings (ADR-0036) and the traceability of ADR-0044. The supplier management pack's decision 2 stands: no version tables.

## Decision

### Change history is part of the audit trail

- An entity declares itself tracked with `ITrackedChanges`. The model check fails, naming the entity, for a business record without it (TF-062 AC2). The kernel entities people change are tracked too: people, roles, assignments, units, settings and company logins.
- On every save, the data layer's interceptor takes each tracked entity the save creates, changes or deletes, with every changed field's old and new value, and in the same transaction writes one audit event per record: `record.created`, `record.changed` or `record.deleted`, with the record's type, id and new version, the actor, the person acted for or the impersonator, the reason when one was given, and the correlation id. A save whose history cannot be written is rolled back.
- A field marked `[Sensitive]` keeps its last four characters (`****3000`); a `[Secret]` field keeps only that it changed. `[NotTracked]` leaves out what other events already tell (the sign-in counters, the recovery fields), and the bookkeeping columns (stamps, version, soft delete) are never history.
- The field list is a column of the audit event, hashed by the canonical form's **version 2**: version 1 with the label `iron-audit-v2` and the changes between the details and the previous hash. Events sealed with version 1 keep it and still verify; the verifier knows both. Why: Part 11 asks for a secure, computer-generated trail, and a second, unchained store would be weaker than the chain.

### A signature keeps a snapshot of what it signed

- A signature stores the signer, their full name and login identifier at signing, the method, the server's time, the meaning, an optional comment, the record's type, id and version, and the record's **snapshot** at that version with its SHA-256. It is written once and never changed.
- The snapshot is the record's content written by its module (`ISignableRecords`) in a documented canonical form (`iron-snapshot-v1`: the label, the record type, the id, the version and the content object, by the same canonical JSON as the audit event's). The record type, id and version are inside the hash, so a signature cannot apply to another record or another version: an attempt to attach it elsewhere is refused because the hashes differ, and recorded (TF-219 AC2).
- Only signed versions are kept, and only because a signature needs them; this is not a version table.
- Reading a signature recomputes the hash from the kept snapshot (TF-063 AC2) and compares the record's current snapshot: a changed record shows the signature against its version, with the signed snapshot one link away (TF-063 AC3).
- Signing is an audit event (`compliance.signature_signed`), chained like the rest.

### The components rule (Part 11 section 11.200)

- The first signature of a continuous session asks for both components, the login identifier and the password, and the second factor when the person has one.
- Later signatures in the same session ask for the password, and the second factor when the tenant's `compliance.signing.later_second_factor` is on.
- A session ends at the tenant's idle limit, so a person back after it signs in again, and the new session starts at the first rule.
- A person without a password, or held to their company login, signs after a fresh sign-in at the provider within the step-up window (TF-248 AC6); each signature takes its own.
- Identity proves the signer behind `ISignerAuthentication`, declared in Compliance's contracts; Compliance never reads a credential. A wrong identifier or password counts against the login as a failed sign-in does, in its own transaction, so it counts whatever happens next.

### Meanings and policy

- The meanings are approved, rejected, acknowledged, signed and reviewed. A document type allows some of them.
- A tenant's policy per document type (`compliance.signing.policies`): whether a signature is required, which meanings it may have, and whether signing asks for re-authentication (TF-063 AC1). Without re-authentication the method is `session`. An always-signed type, the audit-trail review, keeps both whatever the tenant sets.
- A module signs inside its own action: it proves the signer before its transaction, then records the signature with the snapshot in it, so the decision and its signature commit together. Access review decisions are signed approved or rejected when the tenant's policy says so, one by one.

### What may never sign

- Nobody signs while impersonating, while acting for someone, with a token, as a service account, or as an operator's break-glass account. Each refusal is recorded (`compliance.signature_refused`, high severity) before any credential is checked.

### Audit-trail reviews

- A permitted person records the period, the scope (units, event types), the outcome (no findings, or findings with notes) and links to the searches they ran, and signs it "reviewed". The console lists the last months with the reviews that cover each, so a missing month shows. Scheduling reviews as tasks comes with S5's task engine.

### Login identifiers are never reused

- The database reserves every principal's login identifier when the principal is inserted (a trigger on `kernel_identity.principals`), in `login_id_reservations`, and the reservation outlives the principal. An identifier never changes; the trigger refuses a change. Creating a person with a reserved identifier is refused with its own message (TF-219 AC4).

### Where it lives

- `Iron.Kernel.Compliance` (`kernel_compliance`), planned in the PRD's module table for signature manifestation and audit-trail review, holds signatures, the policy and the reviews. Audit keeps the chain and the field history; Identity proves the signer and keeps the identifiers.

### Event types as words

- Every audit event type has a name, declared beside its constant and listed by its module (`IModule.AuditEventTypes`); the audit trail, the History tab and the export show it (TF-064 AC4). A test fails for a constant without one.

## Options considered

- A history table per module, or one history store beside the trail: weaker than the chain, and a second place for an auditor to trust. Not chosen.
- Version tables for every business record: refused by the supplier pack's decision 2; only signed versions are kept.
- The signature bound to the version number alone: a number says nothing of the content, and a copy to another record could not be told from the real one. The hash of a snapshot that names the record is the binding.
- Re-authentication through the existing step-up (a recent sign-in within minutes): Part 11 asks for the components at the signing itself, and for both at the first; the step-up window does not say which. Not chosen.
- Signatures in the Audit module: Audit is the trail; the PRD placed manifestation and reviews in Compliance, and T8's evidence joins it there.

## Consequences

- Every tracked save writes an audit event in its transaction; a save of many tracked records writes as many events.
- A business module declares `ISignableRecords` for each type it lets people sign and keeps its snapshot's fields stable: a field added later changes the snapshot of later signatures only.
- PDF output of the signature block comes with S4's document templates; the print view is the manifestation until then.
- The developer guide's "Signing a record" says how a module signs; the Audit README holds both canonical forms.
