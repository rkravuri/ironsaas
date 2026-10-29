# Slice S4: platform services

The fourth slice (the technical PRD's section 7.1): files with virus scan, notifications with templates and preferences, jobs with a console, the outbox and webhooks, the import and export framework, localisation with a second language, and the forms engine with answers mapped to records and one-time verified access for external respondents. S4 also sends the emails S3's exceptions wait for. This file is the skeleton S3-close left: the requirements, what earlier slices built towards them, and an empty task table. The design session writes what S4 proves, its design and its tasks.

## 1. What S4 proves

To be written in the design session.

## 2. Requirement coverage

| ID | Requirement | Priority | Built before S4 | Notes for the design session |
|---|---|---|---|---|
| TF-078 | Anti-tampering of files and uploads | Must | File names as display text (S3 T9a, ADR-0053) | Moved from S3's range with the files it protects |
| TF-081 | Tenant-scoped data access | Must | One database per tenant, schemas per module (S1) | |
| TF-082 | Migrations | Must | Generated migrations per module, run by the fleet's update job (S1, ADR-0008) | |
| TF-083 | Optimistic concurrency everywhere | Must | | |
| TF-084 | Transactions, locks and atomic checks | Must | One unit of work with its audit event (ADR-0013) | |
| TF-085 | Transactional outbox | Must | | |
| TF-086 | Bulk loading | Should | | |
| TF-087 | Backups and point-in-time recovery | Must | The store persists on the laptop (S2 P1, ADR-0028) | The drills come with the first Azure deployment (S3 T8, ADR-0052); the design session places what the laptop can prove |
| TF-088 | Reference data and seeding | Must | Each module's seed, run by the fleet (S1, ADR-0014) | |
| TF-089 | Data export and portability | Should | Role export and import (S2 A2), the audit trail's CSV (S3 T6) | |
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
| TF-150 | Notification templates and channels | Must | The header's notification place (S2 D4) | |
| TF-151 | Preferences, mandates and digests | Must | | |
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

## 4. Task breakdown

| Task | What it builds | Requirements | State |
|---|---|---|---|

## 5. How we build

As in S3: each task is one Claude Code session from its section in `docs/prompts/S4.md` (ADR-0023), on its own branch from main, never stacked, and reaches main through a pull request with every check green (ADR-0055). The design session writes each section just before its task starts.
