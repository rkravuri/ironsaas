# Threat model: jobs, the runner and the job console

- **Modules:** jobs
- **Endpoints:** none
- **Reviewed:** 2026-10-31 (S4 T2)

## What it protects

The work the platform does without a person waiting (ADR-0061): the audit chain's sealing, retention and archive, the
events' dispatch and archive, key re-encryption, the alert rules, evidence packs, and every module's and suite's jobs.
Each job's record says whether it is paused, by whom and why; each run keeps its key, attempts, error, a log of at most
8,000 characters, who started it and why, and the trace of the request that started it. A paused or failing job is
housekeeping that does not happen: the chain unsealed, alerts not raised, dead letters piling up.

## Who can reach it

Modules declare jobs in code; nothing outside the unit's processes declares or runs one. The unit's worker runs them,
and any second worker shares them through the unit's database, which is its tenant's only. Code starts a run through
`IJobStarter`, inside its unit of work. People reach the jobs through the API: with `jobs.jobs.read`, a signed-in person
or service account sees the jobs, their runs and logs, and the dead letters of jobs (and of events, with
`events.events.read` too); with `jobs.jobs.manage` (high), a recent sign-in and a reason, they pause, resume, start a run
now, and run a dead letter again. The operator does the same for a tenant through the control plane, as a fleet job
running the unit's `jobs` command, with a reason in both trails. The control plane's own jobs run in the fleet.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | A request starting work as someone else, or a run claiming a person started it. | Only a module's code or the console starts a run; the starter takes the person from the request, never from the body, and a scheduled run names nobody. The operator's actions name the operator as the control plane knows them. | TF-120, TF-121 |
| Tampering | Two workers running one occurrence, a run's effect applied twice, or a run's record changed by hand. | One occurrence is queued under the job record's row lock with a unique key per job, and runs are claimed under one lock, so two workers never take one run (TF-120 AC1); a job is idempotent by its declared key, so a retry, a re-run or a recovered run has the effect of one (TF-120 AC3); no endpoint edits a run. | TF-120 AC1, TF-120 AC3 |
| Repudiation | Denying that a job was paused, started or run again, or that a run failed for good. | Pause, resume, run now and run again are audited with the person (or the operator) and the reason, the operator's in both trails; a dead letter and a missed schedule are audited at high severity, which raise alerts. | TF-121, TF-123 AC2, TF-123 AC3 |
| Information disclosure | A run's log or error carrying personal data or secrets to someone who may see jobs; a person without the permission reading jobs. | A job logs what it did by identifier and count, never a person's data or a secret; the error is the handler's message, at most 1,000 characters; the console needs `jobs.jobs.read`, and event dead letters need `events.events.read` too; the operator's list answers states, times and errors, never a log or a payload. | TF-121 AC3 |
| Denial of service | One tenant's bulk work starving another's; a run that never ends; a failing job retried without end; housekeeping paused and forgotten. | Each unit runs in its own containers with CPU and memory limits and a connection limit on its database role, so one tenant's work cannot take another's (TF-122 AC1); within a unit, interactive work is taken before normal and bulk (TF-122 AC2); every run has a timeout, after which it is cancelled and retried (TF-120 AC2); retries end in a dead letter with an alert (TF-123); a schedule more than one interval late is alerted; a paused job shows its state and reason on the console. | TF-120 AC2, TF-122, TF-123 |
| Elevation of privilege | A job doing what the person who started it could not, or pausing used to switch off a control. | A run runs as the system with only what its module's code does, never with the starter's grants; pausing a job needs the high-risk permission and a recent sign-in, never while impersonating, and is audited; a job that runs in each process cannot be paused. | TF-121, TF-248 |

## What remains

- A job that calls outside the unit must be idempotent there too; the run's key makes it run once per occurrence in the
  unit, not once in the world.
- A suspended tenant's jobs are skipped once the control plane writes the suspension into the entitlements document; the
  operation that suspends a tenant is TF-004's, in S6.
