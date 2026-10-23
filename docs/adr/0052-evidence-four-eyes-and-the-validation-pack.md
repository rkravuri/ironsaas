# ADR-0052: Evidence packs, four-eyes on sensitive configuration, release versions and the validation pack

**Status:** Proposed
**Date:** 2026-10-23
**Decided by:** Product owner and architect, in the brief of S3 T8; the details here are the task's, for the design session's review

## Context

S3 T8 builds three things a regulated customer's auditor asks for. An evidence pack for a period (TF-067): the access list, the administrative actions, the security settings, the audit chain's verifications and the backup drills. Proof that sensitive configuration cannot be changed by one administrator alone (TF-068). And, for every release, a validation pack that traces every requirement to its tests and results, with the release's documentation and its installation record, which a tenant downloads for the version it runs (TF-218).

It rests on the sealed audit chain and its anchors (ADR-0050), the field-level history in that chain and electronic signatures (ADR-0051), step-up and the people who may never prove an identity that is not theirs (ADR-0047, ADR-0049), the traceability matrix (ADR-0044), the units that serve without asking the control plane (ADR-0018), and the pipeline (ADR-0022). The brief set three rules: packs carry their own proof; four-eyes is the first form of TF-247's governed changes, built so S5 extends it; every release has a version and every tenant knows its own.

## Decision

### 1. Packs carry their own proof

- **Every pack has a `manifest.json`**: each file's path, SHA-256 and size, and, for an evidence pack, the tenant, the period, the requester by id and name, when it was asked for and made, and the audit chain's head with that position's anchor in the control plane: segment, position, hash, sealed time, and the anchor's id, position, hash and times. An auditor recomputes every hash from the ZIP and matches the head to the anchor and to the tenant's sealed trail.
- **The head is the latest anchored seal**, not the latest event: a pack names a position the control plane already holds. Without a control plane (a host run from source) the pack names the latest seal and says it is not anchored.
- **The ZIP is the same for the same files**: entries in path order with a fixed time, so a pack's hash depends on its content only.

### 2. Evidence packs (TF-067)

- **`compliance.evidence.export`, high risk**, asks for a pack for a period; anyone without it is refused (AC3). The pack holds the whole tenant whatever its requester may read elsewhere, which is why the permission is high risk.
- **The unit's worker makes it**, as the system, on the requester's behalf, a few seconds after the request; the console shows it as requested, being made, ready or failed. A period ending later than now ends now; a period is at most ten years.
- **Each module contributes its own tables** through `IEvidenceSource` in Compliance's contracts, which Compliance gathers by document: Identity the people and service accounts, the company logins without their secrets, the emergency accounts and the tenant's documents in force; Authorization the roles and scopes at the period's end, the grants and removals in the period with who made them, the roles with their permissions, and the conflict rules; Settings the security settings (password and lockout, MFA, sessions, impersonation, signing, four-eyes); Audit the administrative actions, retention, the legal holds standing, the chain's verifications and accepted restores, and the drills.
- **"At the period's end" is rebuilt, not guessed.** A tracked record's state at a time is its state now with every save the field history recorded since undone (`IRecordHistory`, `RecordStates.Rewind`, `TrackedFields`): a creation removes the record, a change puts back each field's old value, a deletion brings the record back. Settings come from their own history (TF-058), retention from its change events, legal holds from their placing and release times. The field history began with T7, so a change made before T7 deployed and after a period's end is not undone; each table says so in its note.
- **The administrative actions** are the events whose type is declared administrative (`AuditEventType.AsAdministrative()`), with actor, impersonator, acting for, target, record, reason and chain position. Identity's security events an administrator causes (factors reset, company-login-only set, access revoked, signing in as someone, emergency marks) are now named and marked too. Events older than the online period are in the archive; the table says so and the trail's search reads them.
- **The chain's verifications come from the control plane**, which now answers a unit's own records of a period (`GET /api/v1/audit-verifications?from=&to=`, the unit client's permission) and gives anchors and records their ids.
- **Backups start with the first Azure deployment**: until then the drills document says so in one sentence and lists the restores of the chain operators accepted.
- **Each document is one readable page and one CSV per table** in a numbered folder (`1-access_list` to `5-backup_drills`), each headed by the tenant and the period; CSV cells are guarded against spreadsheet formulas as the audit export's are. PDF comes with S4's document templates.
- **Downloading is a short-lived link**: for the person who asked for it, once, within five minutes; the bytes are checked against the pack's hash before they leave. Asking (actor the requester), making (the system, on behalf of the requester) and downloading (the downloader, the requester in the details) are audit events (AC2).

### 3. Four-eyes on sensitive configuration (TF-068)

- **The module is `Iron.Kernel.Changes`** (`kernel_changes`), the one the technical PRD plans for TF-247's governed changes. Placement: no business meaning, so Kernel.
- **The question is the kernel vocabulary's**: `IFourEyes`, beside `IRecentAuthentication`, with a default that never holds anything (a job, a seed, a host without the module); the Changes module answers it. A module never references Changes to ask.
- **The areas**: roles and assignments, single sign-on, retention, the MFA and password policies (the password, lockout and MFA settings), legal holds, and the signing policy. Entitlements are the operator's and not in the list. A setting joins an area where it is declared (`.GovernedBy(area)`).
- **The tenant lists its areas** in `changes.four_eyes.areas`, the signed ones in `changes.four_eyes.signed_areas`, and the waiting time in `changes.four_eyes.expiry_days` (7, from 1 to 30). **While any area is listed, a change of these settings waits for a second administrator too**: otherwise one administrator could switch the control off and then change alone. With no area listed, one administrator turns it on.
- **A save becomes a pending change** after the module's own checks (validation, nobody grants more than they hold, the conflict rules, step-up where the action asks it) and before it writes: the area, what it does, its subject, a title, the state before and after as people read them, the payload the module needs to apply it, the requester and reason, the permission an approver needs and where. The API answers **202** with `error_code` `changes.change.held` and the change's id: an error kind `Held`, not a failure. Nothing applies (AC1). One change waits per subject (a partial unique index); a second save of the same thing is refused naming the first.
- **A company login's new secret never enters the change's row**: it waits in the store under a staging id, moves to the provider when the change applies, and leaves the store when the change ends otherwise.
- **Another person decides**, holding the change's permission at the same place (a grant's unit, the tenant for the rest), with a reason for a rejection, after step-up, or with an electronic signature meaning `approved` or `rejected` where the area is signed or the signing policy requires one for `configuration_change` (T7). **Refused, and audited at high severity before anything else is checked**: the requester (AC2), anyone signed in as someone else, acting for someone, a service account, and an operator's break-glass account (`ICurrentPrincipal.IsBreakGlass`). The requester may withdraw.
- **On approval the subject is compared first**: the owning module answers its current state, and when it differs from the change's before, something else changed it meanwhile. The change does not apply; it takes the current state as its new before, counts a review more, keeps a note of both values, and waits for a new decision. Otherwise the module applies it through the same path a direct save takes, **as the approver**, whose own rules hold (an approver who may not grant the role leaves the change pending), in the transaction that records the decision; the module's events and field history name the approved change in their reason, and `changes.change_approved` names both people (AC3).
- **Not held**: an access review's revoke, which is a decision of the review with its own reviewer and record (ADR-0037); the system principal's work (seeds, jobs); and the operator's commands.
- **A change nobody decides expires** after the tenant's period, by the worker every five minutes and by every list and decision; nothing applies.

### 4. How TF-247 extends it in S5

The pending change is TF-247's change request in its first form; S5 extends the same record and contract rather than replacing them:

- The area and the "another administrator with the permission" rule become the module's field classes (uncontrolled, controlled, critical, with a floor a tenant may raise and never lower) and the workflow engine's routing by the document type's approval configuration (TF-090). The configuration areas stay as the classes of the kernel's own configuration.
- A change's before and after become field-level changes, each with its old and new value and its own decision; a request may end partly applied. One pending change per subject becomes one per field.
- The comparison before applying becomes TF-247 AC5's per-field check, returning the field to review naming both values, as the whole change does now.
- An effective date makes the approved change wait, applied by a job (TF-120) on its day.
- The applier contract (`IConfigurationChangeApplier`: the subject's state now, apply, discard) gains the module's hooks: a verification before a critical field applies, the notifications after, the impact preview.
- Nobody decides their own change stays TF-036's rule; the refusals of impersonators, people acting for someone, service accounts and break-glass accounts stay.

### 5. Release versions

- **A release's version is the UTC date its run started and the release workflow's run number**: `2026.10.23.57`. The run number grows with every run, so versions always increase and never repeat; a failed run leaves its number unused. Re-running failed jobs keeps the version job's answer.
- **The release workflow stamps it into every image** (the build argument `IRON_VERSION`, the label `org.opencontainers.image.version`, and for the .NET hosts `Release__Version`), and, when the release passes, tags the commit `v<version>` and the images `:<version>`.
- **The unit names its version** in `/me/context` (`release`), which the shell's help panel and the console show; an image built on a laptop has none and is a development build. The API image answers `version` and `configuration` without building a host, for the fleet and the installation record.

### 6. The validation pack (TF-218)

Built by the release workflow (`tools/validation_pack.py`, Python's standard library, beside the trace) from the run's matrix and history, into `validation-pack-<version>.zip`:

- **traceability**: every live requirement of every requirements document with each criterion, the tests that prove it and their results in this run, and its state: passed, failed, not run, excepted (with the approved exception), a **gap** (a done slice's criterion with no test and no approved exception), or untested (outside a done slice) (AC1); also as CSV.
- **release notes**: the version, the commit, the merged tasks and the commits on main since the last release tag, each with the requirements it touched: those whose tests it changed, whose text it changed in a requirements document, or which its commit messages name.
- **risk assessment**: each change classed by the highest risk of the requirements it touched. A requirement is **critical** where its area is listed in `docs/trace/risk.md` (data integrity, access and the controls a validated system relies on), **high** for any other Must, **normal** for a Should or a Could; a change that touched none is normal and shows how many files it changed. The written assessment is the one its task committed at `docs/risk/<task>.md`, which every task report's Risk section also states (CLAUDE.md).
- **installation record** (`tools/installation_record.py`, after the integration stage's smoke and browser tests): the images' digests, the version the API image says, the migrations applied per module's schema in the tenant's database, the seeds as the database holds them, the catalogue's, seeds' and permissions' hashes, and the smoke's and browser tests' results, with the checks against the code: the version is the release's, the catalogue in the image is the repository's, every schema holds exactly the migrations the repository has for it, the seeds are the ones the code makes (a canonical text of the system roles and their permissions, hashed alike from the image and the database), and the smoke and the browser tests passed.
- **configuration**: the catalogue's hash from the repository and the hashes the installation measured, so a tenant sees its controlled configuration.
- a **manifest** of every file with its SHA-256, the version, the commit, the previous release and the verdict.

**The pack is always written, and the release fails** (no tag, no GitHub release) on a gap without an approved exception (AC2), a failed test, a problem the trace found, or an installation check that did not pass; the pack's index says why. It is kept with the run for 90 days, as T1 keeps the matrix, and with the GitHub release of a passing version for good.

### 7. Where a tenant gets it

- **The releases container** of the platform's storage keeps each version's pack under `<version>/`; the fleet's image publishes one (`publish-validation-pack <version> <file>`): a deployment with the release's pack, `make validation-pack` on the laptop with the development pack.
- **At each provisioning and update, the fleet's ninth step** asks the API image its version and copies the pack of exactly that version into the tenant's own container (`validation/<version>/`). A pack not published yet never stops an update, which must heal a unit; the step says so and the unit says the pack is missing.
- **The console's Validation page** (`compliance.validation.read`) shows the installed version and downloads its pack from the tenant's container, without asking the control plane (ADR-0018); the download is audited (AC3). A development build serves the latest local pack, if one was placed.

### 8. The matrix jobs and the task reports

- **Every artifact download of the matrix jobs is tried three times** with a pause between (a composite action, `.github/actions/download-artifacts`, and a loop for `gh run download`), after CI run 39912627606 lost its connection while downloading. The release's matrix job concluded and kept its artifact in each of the twelve release runs before T8 (M4's later item, checked).
- **Every task report has a Risk section**: the requirements the task touched, their class, and a short assessment of what could go wrong and what proves it does not, committed as `docs/risk/<task>.md` for the release's risk assessment.

### 9. In the shell

- **A held change is thrown, not returned.** The client turns a 202 problem with `changes.change.held` into an `ApiError` whose `isHeld` is true and which carries the change's id, so no page reads it as done and goes on. A form shows it in the information colour, with a link to the change, not as a failure; the frame shows the same notice for an action outside a form (a removal, an import) and the few such actions in governed areas leave their failure colour to real failures.
- **Pending changes are in the console** (`/console/changes`): to approve (the default), mine, and every one with `changes.changes.read`, which is also the navigation entry's permission. The routes ask only for a signed-in person, because the API decides which changes each sees; requesters and approvers without the read permission reach them from the notice, a link or the address.
- **A change's page shows its state before and after side by side**, one row per field, the changed fields marked in words, nothing before meaning made and nothing after removed. Approve and reject take a reason first, then the signing dialog where the change is signed (`configuration_change`, meanings `approved` and `rejected`, the reason kept as the signature's comment on the server); the requester sees only Withdraw.
- **The four-eyes area lists are chosen by their words** in Settings, not typed as JSON.
- **Evidence packs are in Compliance** (`/compliance/evidence-packs`, `compliance.evidence.export`): a period of whole UTC days, the pack's page reading itself again every two seconds until it is made, its hashes and the chain head with its anchor, and a download through a link the API makes for that person.
- **The Validation page is in Compliance too** (`/compliance/validation`, `compliance.validation.read`), on the console template; the help panel shows the version for everyone signed in.

## Options considered

- **The evidence state at the period's end as the state when the pack is made**: simpler, but not what the auditor asked for. Rebuilt from the field history the trail already holds.
- **One evidence service in Compliance reading every module's tables**: crosses every module's boundary (ADR-0001). Each module contributes through a contract.
- **Four-eyes in Compliance, or in each module**: the PRD plans `Iron.Kernel.Changes` for governed changes; one module keeps the rule in one place and is what S5 extends.
- **Four-eyes at the endpoints**: a second path to the same save (an import, a future job) would bypass it. The services ask, before they write.
- **A stale change stopped for good**: the brief's "stops for a new review" read as a new review of the same change against the values now; the requester does not have to ask again.
- **Semantic versions**: say what changed, which the validation pack's notes and risk already say; a date and a run number is readable, always increasing and needs no judgement in the pipeline.
- **The fleet fetching packs from GitHub's releases**: the registry is private and the fleet carries no credentials (ADR-0022); the platform's storage is where the fleet reaches.
- **The validation pack as a .NET tool**: the trace it builds on is Python's standard library (ADR-0044); the pack joins it.

## Consequences

- An auditor gets the five documents of any period in one ZIP that proves its own integrity and its place in the sealed trail.
- A tenant that lists an area cannot have it changed by one administrator, nor by an impersonator or an operator's break-glass account; every save in the area waits, which slows routine work in it by design.
- Every release is traceable from its version: the tag, the images, the GitHub release with its pack, and in each tenant the pack of the version it runs.
- A release with a gap does not publish: the exceptions list (`docs/trace/exceptions.md`) is the only way through, as for the matrix.
- Revisit: the drills when the first Azure deployment brings backups; the pack's PDF with S4's templates; four-eyes as TF-247 in S5; the releases container filled by the Azure deployment's pipeline; a pack for a period older than the online audit trail reading the archive in full.
