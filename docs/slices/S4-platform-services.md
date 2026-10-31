# Slice S4: platform services

The fourth slice (the technical PRD's section 7.1): files with virus scan, notifications with templates and preferences, jobs with a console, the outbox and webhooks, the import and export framework, localisation with a second language, and the forms engine with answers mapped to records and one-time verified access for external respondents. S4 also sends the emails S3's exceptions wait for. This file is the skeleton S3-close left: the requirements, what earlier slices built towards them, and an empty task table; V-close added what V moves to it (section 4). The design session writes what S4 proves, its design and its tasks.

## 1. What S4 proves

To be written in the design session.

## 2. Requirement coverage

| ID | Requirement | Priority | Built before S4 | Notes for the design session |
|---|---|---|---|---|
| TF-078 | Anti-tampering of files and uploads | Must | File names as display text (S3 T9a, ADR-0053) | Moved from S3's range with the files it protects |
| TF-081 | Tenant-scoped data access | Must | One database per tenant, schemas per module (S1) | |
| TF-082 | Migrations | Must | Generated migrations per module, run by the fleet's update job (S1, ADR-0008); a suite's only where it is installed (V2, ADR-0059) | |
| TF-083 | Optimistic concurrency everywhere | Must | | |
| TF-084 | Transactions, locks and atomic checks | Must | One unit of work with its audit event (ADR-0013) | |
| TF-085 | Transactional outbox | Must | | |
| TF-086 | Bulk loading | Should | | |
| TF-087 | Backups and point-in-time recovery | Must | The store persists on the laptop (S2 P1, ADR-0028) | The drills come with the first Azure deployment (S3 T8, ADR-0052); the design session places what the laptop can prove |
| TF-088 | Reference data and seeding | Must | Each module's seed, run by the fleet (S1, ADR-0014); a suite's seeds only where it is installed, and the seeds' hash per set of installed suites in the installation record (V2, ADR-0059) | Section 4 |
| TF-089 | Data export and portability | Should | Role export and import (S2 A2), the audit trail's CSV (S3 T6) | An export of records under the record policy, tags included: section 4 |
| TF-110 | Domain event catalogue | Must | Audit event types declared with their words (S3 T7, ADR-0051) | |
| TF-111 | Event subscriptions inside the platform | Must | | |
| TF-112 | Event replay and inspection | Should | | |
| TF-113 | Message broker readiness | Could | | |
| TF-114 | Outbound integration connectors | Should | | |
| TF-120 | Background jobs | Must | The unit's worker with its hosted services (S1, S3) | TF-003 AC2 and TF-130 AC3 wait for a tenant's job and a job a request starts |
| TF-121 | Job console | Must | | |
| TF-122 | Per-tenant fairness and priority | Should | | |
| TF-123 | Retries, dead letters and alerts | Must | | |
| TF-124 | Maintenance jobs supplied by Foundation | Must | The retention job, the audit sealing and the re-encryption (S3 T6, T9b) | |
| TF-131 | Metrics and SLIs | Must | OpenTelemetry metrics to Prometheus (S1); the lost spans' counters (S3 T10b) | |
| TF-132 | Structured logs with scrubbing | Must | Logs to Loki with personal data and secrets masked (S3 T6, T9b) | |
| TF-133 | Health checks | Must | Readiness with each module's checks (S1) | |
| TF-135 | Client-side telemetry | Should | | |
| TF-136 | Usage analytics for product teams | Could | | |
| TF-140 | Externalised UI strings and translation workflow | Must | Every shell string in the catalogue (S2, ADR-0024) | |
| TF-141 | Server messages and errors localised | Must | | The audit event types' and permissions' words are the server's English (S3 T7) |
| TF-142 | Locale formats | Must | Every time in the person's zone (S3 T6) | |
| TF-145 | Unicode and locale-aware text handling | Must | | |
| TF-150 | Notification templates and channels | Must | The header's notification place (S2 D4) | A notification that names a record goes only to its readers: section 4 |
| TF-151 | Preferences, mandates and digests | Must | | A digest leaves out what its reader may not see (TF-243 AC3): section 4 |
| TF-152 | Delivery tracking and resend | Must | | TF-018 AC3's resend of an invitation |
| TF-153 | In-app notification centre and real-time push | Must | | |
| TF-154 | Email sending infrastructure | Must | The email kill switch declared (S2 A5) | TF-051 AC1 and AC2 wait for it |
| TF-155 | SMS and other channels | Should | | TF-013's fallback by SMS |
| TF-156 | Announcements | Should | | |
| TF-161 | Upload rules | Must | The storage interface and its readiness (S1) | TF-160 AC2 and AC3 wait for uploads and versions |
| TF-162 | Document rendering | Must | The print view of the record template (S3 T7) | The PDFs of the evidence pack (TF-067) and of the signature block (TF-219) |
| TF-163 | Document templates management | Should | | |
| TF-164 | Signature capture and signed documents | Should | Electronic signatures bound to their version (S3 T7, ADR-0051) | |
| TF-165 | File retention, archival and purge | Must | Retention classes and legal holds (S3 T6, ADR-0050) | |
| TF-166 | Document numbering service | Must | | TF-221 AC1 waits for it |
| TF-206 | Connector configuration for external systems | Should | | Moved from S7 at the owner's decision of 2026-10-30, for Supplier Management's identity, bank account and sanctions checks (T9) |
| TF-242 | Forms engine | Should | The schema form of the design system (S2 D3) | |
| TF-245 | Mapping form answers to records | Should | | Change proposals before S5's governed changes (TF-247) |
| TF-246 | One-time verified access for external respondents | Should | | Lets supplier information collection ship before the Supplier Network (S8) |

## 3. The emails and documents S3 left to S4

Each an exception or a listed part in `docs/trace/exceptions.md`, approved at S3-close's review:

- the lockout's email (TF-012 AC2), a person's own recovery link to their verified email (TF-017), the invitation by email and its resend (TF-018, AC3), the security events' emails with their link to the sessions (TF-019 AC1 to AC3), the email to a person impersonated (TF-025), and a pending change's notices (TF-068);
- the MFA fallback by one-time codes by email or SMS (TF-013) and the emails for a new factor, a removal and a reset (ADR-0047);
- the escalation of a late access review sent to the reviewer's manager (TF-038 AC3);
- the evidence pack's PDF (TF-067) and the signature block in PDF (TF-219), with the document templates;
- a tenant's branding in its emails and the logo upload (TF-173 AC1 to AC3);
- the first rich-text fields, shown through `RichTextView` (S3 T9a).

## 4. What V moves to S4

V built visibility tags (TF-241, ADR-0057) and suites per tenant (TF-244, ADR-0059). Until S4 nothing sends, attaches, exports or imports a business record, and every per-module service is a migration or a seed, so neither mechanism has yet met a service that could go around it. S4 is the first slice whose services carry records and run per module. Each item with its reason, for the design session to place in S4's tasks:

- **A notification that names a record goes only to people who may read it** (TF-150, TF-151). Its readers are decided as a list's are, by the record's policy, its unit and its visibility tags, and a digest leaves out a record its reader may not see. TF-243 stays S5's with the approvals and tasks of the workflow engine, but its digest criterion (AC3) is first met by S4's digests, so S4 builds them to it. Reason: V1's later item, "until then nothing sends a person a record outside their tags, since nothing sends records", stops being true with S4.
- **Attachments, exports and imports of records go through the record policy** (TF-160 AC2, TF-161, TF-086, TF-089). An attachment is read by those who may read its record, tags included; an export of records holds the rows its reader's list holds; an import that sets tags is held to rule 5 through `IRecordTags`. Reason: each is a second way to a record, and the policy decides access only where it is asked (ADR-0032, ADR-0057).
- **Forms on records** (TF-242, TF-245): a form instance about a record goes only to people who may read the record, and answers mapped to it propose changes its policy allows. Reason: as notifications.
- **A suite's services exist only where the suite is installed** (TF-082, TF-088, TF-110, TF-120, TF-140, TF-150, TF-242). A suite's notification templates, event subscriptions, jobs, strings and form templates are registered by its modules, so a unit without the suite has none of them, and the Foundation names none of them (ADR-0043, decision 8). Removing a suite leaves its data, never running work: its jobs and subscriptions stop with its modules. Reason: V2 made migrations, seeds and endpoints follow `Suites:Installed`; S4's services are the next things a module registers.
- **The seeds per composition** (TF-088, TF-218). The installation record checks a unit's seeds against the hash of its composition, one per set of installed suites (V2). S4's reference data keeps that check; with a third suite the hashes become one per suite, combined (STATUS). Reason: S4's reference data is what the seeds become.

## 5. Task breakdown

The tasks of `docs/prompts/S4.md`, one section each; T1 was written in full, T2 to T9 as outlines the design session sharpens before each starts.

| Task | What it builds | Requirements | State |
|---|---|---|---|
| T1 | Domain events, the transactional outbox, subscriptions, replay and inspection | TF-085, TF-110 to TF-113 | done (pull request #32, ADR-0060) |
| T2 | Background jobs, the job console, fairness, retries and dead letters, maintenance jobs | TF-120 to TF-124; S3's exceptions TF-130 AC3 and TF-003 AC2 | outline |
| T3 | Files: upload rules, virus scanning, retention and purge; attachments through the record's access policy | TF-078, TF-161, TF-165 | outline |
| T4 | Notifications: templates, channels, preferences, digests, delivery tracking, the in-app centre with live push, email, announcements; every email S3 left waiting | TF-150 to TF-156 | outline |
| T5 | Documents: PDF rendering, templates, signature capture, numbering; the PDFs S3 left waiting | TF-162 to TF-164, TF-166 | outline |
| T6 | Localisation: a pseudo-language in every build and German as the first real second language, server messages, locale formats, Unicode | TF-140 to TF-142, TF-145 | outline |
| T7 | Import, export and reference data through the record's access policy; bulk loading; seeds per suite | TF-086, TF-088, TF-089, TF-205 | outline |
| T8 | The forms engine, with surveys, mapping answers to records, and the one-time verified link | TF-242, TF-245, TF-246 | outline |
| T9 | Outbound connectors and webhooks, with the connector kinds Supplier Management needs (identity and tax status, bank accounts, sanctions) | TF-114, TF-206 | outline |
| S4-close | Exceptions, documentation, closing notes | | outline |

### The owner's decisions for S4 (2026-10-30)

- TF-206 (connector configuration for external systems) moves into S4, in T9, because Supplier Management's checks of identity and tax status, bank accounts and sanctions need it before S7.
- A pseudo-language in every build, and German as the first real second language (T6).
- PDF rendering is chosen in T5 by measurement, under the open-source rule (ADR-0003).
- Mailpit is the laptop's mail catcher (T4).
- ClamAV runs in its own Compose profile, started for the file tests and the pipeline, not with the everyday stack (T3).
- The rule V carries into every task: whatever S4 sends, attaches, exports, imports or asks about concerning a record goes only to people who may read that record, visibility tags included; and whatever a suite declares (templates, events, jobs, strings, forms, reference data) exists only where the suite is installed (section 4).

## 6. How we build

As in S3: each task is one Claude Code session from its section in `docs/prompts/S4.md` (ADR-0023), on its own branch from main, never stacked, and reaches main through a pull request with every check green (ADR-0055). The design session writes each section just before its task starts.
