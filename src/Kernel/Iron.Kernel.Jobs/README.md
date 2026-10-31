# Iron.Kernel.Jobs

**Layer: Kernel.** Running background work has no business meaning of its own: a module's job is the module's, and this
module only runs it (ADR-0029). Schema `kernel_jobs`.

The job runner, its records and runs, retries and dead letters, the job console and the operator's `jobs` command
(TF-120 to TF-124, ADR-0061). How to write a job is in the developer guide's "Writing a job".

| Folder | Holds |
|---|---|
| `Contracts/` | `JobsPermissions` (`jobs.jobs.read`, Medium; `jobs.jobs.manage`, High), `JobsAuditEvents` (`jobs.job_paused`, `jobs.job_resumed`, `jobs.run_triggered`, `jobs.run_rerun`, administrative; `jobs.run_dead_lettered` and `jobs.schedule_missed`, High, which the alert rules count), `JobsErrors`, `JobsSettings` (`jobs.time_zone`, the cron schedules' zone, UTC by default; `jobs.history.days`, how long finished runs stay, 14), `RunStates` |
| `Data/` | `JobsDbContext`; `JobRecord` (a declared job's state: paused with who and why, its next due time, the last occurrence queued); `JobRun` (a run: its key, unique per job, status, priority, attempts, due, start and end, the holder's lease, error, log, who started it and why, its payload, the trace it was started in, dead or held since when, re-runs); the design-time factory, generated `Migrations/` |
| `Services/` | `JobRunner` (one per worker process, ticking every half second), `JobRunnerService` (its loop in the worker), `JobRecordsSync` (a record for every declared job), `JobStarter` (the kernel's `IJobStarter`), `JobConsole` (what the API and the command show and do), `JobZone`, `JobsCommand` (the image's `jobs`), `JobsSeeder` (a new tenant's records, TF-124 AC1), `PruneRunsJob` (`jobs.prune_runs`), `JobsTelemetry` (span source and meter `Iron.Jobs`) |
| `Api/` | `GET /api/v1/jobs`, `/jobs/{name}`, `/jobs/{name}/runs` (the list grammar), `/jobs/runs/{id}` with its log; `POST /jobs/{name}/pause`, `/resume`, `/run` and `/jobs/runs/{id}/rerun`, each with `jobs.jobs.manage`, a recent sign-in and a reason; `GET /api/v1/dead-letters`, the dead letters of jobs and, for a person who may read events, of event subscriptions |

The declarations are the kernel's (`JobDefinition`, `JobSchedule`, `RetryPolicy`, `PriorityClass`, `JobScope`, `IJob`,
`JobContext`, `IJobStarter`, `JobLoop` in Iron.Kernel; `JobDeclarations` and `ProcessJobRunner` in Iron.Kernel.Api), so a
module declares its jobs without referencing this one.

## How a unit runs its jobs

- **Records.** The seed makes a record for every job the unit declares, active and scheduled from now; the runner's
  first tick in a process makes those a newer build brought, holds the queued runs of a suite the unit no longer runs
  and resumes those of a suite it runs again.
- **Once per occurrence.** A scheduled job's due occurrence is queued, and its next time moved on, in one transaction
  under the record's row lock, which a second worker skips (`FOR UPDATE SKIP LOCKED`); the run's key is the occurrence,
  unique per job. An occurrence more than one interval and a minute late passes over the missed ones, runs the latest
  and audits `jobs.schedule_missed`.
- **Claims.** Under one advisory lock, a tick takes back the runs whose lease passed (their holder stopped), then takes
  queued runs, interactive before normal before bulk, oldest first, within each job's concurrency, as many as the
  process has slots for (`Jobs:Runner:Slots`, 16). A paused job's runs wait. A suspended tenant's due occurrences are
  recorded as skipped and nothing runs.
- **A run** is a scope of its own with the handler, cancelled at its timeout, in a span of `Iron.Jobs` with the tenant,
  the job, the run, the attempt and the class, linked to the request that started it. A failure waits the policy's
  growing, jittered delay; after the last attempt it is a dead letter, audited in the same transaction. A worker that
  stops gives its unfinished runs back to the queue, uncounted; one that dies leaves them to their lease.
- **Configuration** (`Jobs:Runner`): `TickMilliseconds` (500), `Slots` (16), `LeaseGraceSeconds` (30),
  `MissedAfterSeconds` (60). The unit's command takes `Jobs:Command` (`Action`, `Job`, `RunId`, `Reason`, `Operator`),
  which the fleet sets as `Jobs__Command__*`.

The unit's worker calls `AddJobRunner()`; a test host that ticks the runner itself calls `AddJobHandlers()`. The API host
runs the module for the console and the per-process jobs, not the runner. The control plane runs neither (ADR-0061).
