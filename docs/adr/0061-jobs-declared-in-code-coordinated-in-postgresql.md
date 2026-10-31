# ADR-0061: Jobs: declared in code, coordinated in the unit's PostgreSQL, retried into dead letters, the worker's loops among them

**Status:** Accepted
**Date:** 2026-10-31
**Decided by:** Product owner and architect, in the brief of S4 T2; accepted at the design session's review of T2 (2026-10-31) without change

## Context

TF-120 asks for jobs defined in code with a schedule or a trigger, a scope, a timeout, a retry policy and a concurrency
rule, run in clustered mode once per tenant per interval, and idempotent. TF-121 asks for a console where administrators
and operators see and steer them, audited; TF-122 for fairness between tenants at the shared resources and priority
classes inside a unit; TF-123 for retries with backoff and jitter into dead letters, and alerts on their growth and on a
missed schedule; TF-124 for the maintenance jobs the Foundation ships. Before T2 the unit's worker ran nine background
loops, each a hosted service of its own with its own timer, its own error handling and no record of a run: the audit
chain's sealing, the events' dispatch and archive, the audit retention, re-encryption, the alert rules and their copy to
the control plane, evidence packs, the four-eyes expiry, the access reviews' escalation and the conflicts report. Two
worker processes would each run every loop; a failure was a log line. A unit serves one tenant (ADR-0002), so a job's
"tenant scope" is its unit, and the tenants share only the database server and the host. TF-003 AC2 and TF-130 AC3 had
exception rows waiting for jobs: a job's queries and telemetry stay in its tenant, and a job started by a request links
to its trace.

## Decision

1. **A job is declared in code, in its module.** `JobDefinition.Create<THandler>` names it, `module.what_it_does`, says
   what it does and who owns it, and gives its schedule (an interval, a cron expression of five fields in the tenant's
   time zone, the setting `jobs.time_zone`, or only when started), its scope, its timeout (at most 24 hours), its retry
   policy, how many runs may run at once (1 to 16) and its priority class. A class that is not defined is refused where
   the job is declared, so a test that builds the module fails (TF-122 AC4). A module lists its jobs in `IModule.Jobs`;
   `make catalogue` writes them into the licensing catalogue, grouped by suite, and the host test that keeps the file
   equal to the code covers them. A handler is an `IJob`, resolved from the run's own scope, which answers a `Result`
   for an expected failure. An interval a module read from its configuration before (`Monitoring:EvaluationSeconds`,
   `Flags:RefreshSeconds`) still sets it, within the bounds the module had.
2. **Idempotent by key.** A run is unique by its job and its key: a scheduled run's key is the occurrence's time, a
   started run's the key its starter gives (an event's id, a run's id and a number). A unique index makes a second run
   with the same key the same run, so a second worker, a retry of the starter or a replayed event adds nothing. The run
   itself must have the effect of one run when it runs twice (a retry after a timeout, a lease taken back): each of the
   Foundation's jobs is written so, and the developer guide's "Writing a job" says how (TF-120 AC3).
3. **Coordination: our own runner on the unit's PostgreSQL, not Quartz.NET.** The candidates were Quartz.NET 3 with its
   clustered ADO.NET job store (Apache 2.0), Hangfire with a PostgreSQL storage (LGPL 3, the storage a third party's),
   and a runner of our own on the database the unit already has.

   | | Quartz.NET, clustered | Hangfire | Our runner |
   |---|---|---|---|
   | Once per occurrence across workers | Yes: row locks on its lock table, misfire rules | Yes: distributed locks in its storage | Yes: the job record's row lock with `SKIP LOCKED`, a unique key per run |
   | Its tables in every tenant's database | 11 (`qrtz_*`), its own migrations | About a dozen, its own migrations | 2, an EF migration like every module's |
   | Starting a run in the change's transaction | No: its own connection | No: its own connection | Yes: the run commits with the change and its audit event |
   | Priority classes across jobs | A trigger priority among triggers firing at once | Queues by name, polled in order | Interactive, normal, bulk on every claim |
   | Run history, run log, dead letters | None kept; a listener writes them | Kept, in its own dashboard | Kept in the unit's tables, shown in the shell |
   | Retry with backoff and jitter | Not built in | Built in, without our policy | The policy of the declaration |
   | Suspended tenant, removed suite | Pause by hand | Pause by hand | Skipped and logged; held and resumed |
   | Audit of every action | Ours to add | Ours to add | In the unit of work, as every module's |

   Both libraries are proven, and both would leave us writing what TF-121 to TF-123 ask on top of them: the history, the
   log, the dead letters, the console, the audit, the priority across jobs, the skip of a suspended tenant and the hold
   of a removed suite. What they would give, the exactly-once claim, is the part PostgreSQL gives directly: a row lock
   that a second worker skips, a unique index, an advisory lock and a lease. Our runner is about 600 lines with that,
   coordinated in the database every module already uses, inside its unit of work, with no second schema or connection
   pool per tenant. Cronos (MIT, `Directory.Packages.props`) parses cron expressions with time zones and daylight saving;
   nothing else is added. The two-worker test proves it: two runners on one database over six minutes of a
   minute's job run six occurrences, each once (TF-120 AC1).
4. **A tick of the runner.** Each worker process runs one runner, ticking every half second (`Jobs:Runner`). A tick:
   makes a record for each declared job at the process's first tick (the seed made them for a new tenant, TF-124 AC1);
   skips every due occurrence, recorded and logged, while the tenant is suspended, which the entitlements say
   (`ITenantStanding`, TF-122 AC3); queues each scheduled job's due occurrence and moves its next time on in the same
   transaction, under the record's row lock; then, under one advisory lock, takes back the runs whose lease passed and
   claims queued runs, interactive before normal before bulk, oldest first in a class, within each job's concurrency
   (TF-122 AC2), as many as the process has slots for (16). A run's lease is its timeout and 30 seconds: a worker that
   died leaves its runs to be taken back by any worker once the lease passed, as a new attempt. A worker that stops, as
   at an update, gives its unfinished runs back to the queue at once, the interrupted attempt uncounted, so the next
   worker takes them without waiting for a lease and a frequent job such as the dispatch pauses for no longer than the
   restart.
5. **A timeout cancels, logs and retries.** A run is cancelled at its timeout, its log says so, and it is retried by its
   policy (TF-120 AC2). A failed attempt waits the policy's first delay, doubled at each failure up to its longest,
   moved by up to a fifth either way at random so runs that failed together do not retry together (TF-123 AC1). The
   default is five attempts, 10 seconds to 10 minutes. After the last attempt the run is a dead letter, with its error
   and its log, audited as `jobs.run_dead_lettered` (High) in the same transaction (TF-123 AC2).
6. **Alerts come from the audit trail, as every alert does (ADR-0056).** `job_dead_letters` raises one at the first dead
   letter of a job; `dead_letters_growing` at ten dead letters of jobs and events in an hour across the unit;
   `job_schedule_missed` when a scheduled job's occurrence ran more than one interval and a minute late. That runner
   passes over the occurrences it missed, runs the latest once and audits `jobs.schedule_missed` with how many it missed
   and how late it was (TF-123 AC3). A housekeeping job's next run catches up; a job whose every occurrence matters says
   so in its key and its runbook. The runbooks are in `docs/security/incident-response.md`.
7. **The worker's loops are jobs (TF-124).** `audit.seal_chain` (sealing, with the chain's verification by the fleet as
   before), `audit.retention` (retention and archive, sessions among the records it keeps and deletes),
   `events.dispatch` and `events.archive`, `keys.reencrypt`, `monitoring.evaluate_rules` and `monitoring.copy_alerts`,
   `compliance.evidence_packs`, `changes.expire_pending`, `authz.escalate_reviews`, `authz.conflict_report`, and two new
   ones: `identity.token_cleanup` (expired sign-in challenges, sign-in links expired 30 days ago, idle counters of failed
   sign-ins) and `jobs.prune_runs` (finished runs past `jobs.history.days`, dead letters kept). Their behaviour and their
   tests are kept; their tests now run the job.
8. **A frequent loop is a term job.** Sealing every five seconds and dispatch every 300 milliseconds as runs of their own
   would write 17,000 and 288,000 run rows a day each. A term job is one run a minute that passes until its term ends,
   every few seconds as the loop did (`JobLoop`): it holds one runner slot, is coordinated, logged, retried and alerted
   like any run, and the work keeps its latency (TF-085 AC1's 10 seconds is still the smoke's bound). Its term ends a
   minute after its occurrence, when the next falls due, not a minute after it started: a run that started late ends on
   time, so a lag never carries from one run to the next. The laptop showed why: counted from the start, every claim and
   every pass that overran added to the lag, which reached 78 seconds for `keys.reencrypt` within minutes. A pass that throws
   is logged and the next one runs; the run fails, to be retried, only when its last pass failed.
9. **What each process keeps for itself is a process job.** The flag, setting and entitlement caches live in each
   process's memory, so no other process can refresh them: their reads after the first are jobs of scope `EachProcess`
   (`flags.refresh_cache`, `settings.refresh_cache`, `entitlements.refresh_cache`), run by every API and worker process
   on its interval with its timeout and retry, recorded in no table; the console lists them as run in each process. The
   first read stays in the host's start, before it serves (ADR-0034 to ADR-0036). The heartbeat and the clock check stay
   hosted services: they report on the process itself.
10. **Fairness between tenants is the fleet's, at the shared resources (TF-122 AC1).** A unit's containers have the CPU
    and memory of its size class (ADR-0018), and now its app role has a connection limit on the database server: 30, 60
    and 120 for small, medium and large, set by the database step at every provisioning and update. Each process's pool
    takes a third (`Maximum Pool Size` in the unit's connection string), the API and the worker and room for a second
    worker, so a full pool waits where a connection over the role's limit would be refused. A tenant whose backlog holds
    every connection it may is refused more, and another tenant still connects: the fleet's test proves it on
    PostgreSQL. The bulk sample job `samples.saturate` queues 10,000 runs that each hold a connection and a processor;
    `tools/measurements/job-fairness.sh` starts it in one unit and times another unit's job.

    Measured on the laptop on 2026-10-31, both units small (`tools/measurements/job-fairness.sh echo acme`): while
    echo's backlog ran eight saturating runs at once with 1,636 more queued, its role holding 12 connections and its
    worker at its limit of half a processor (49 to 51%), acme's three runs started by hand through the fleet waited
    1.3 seconds from queued to started (1,337 ms median, 1,347 the longest), and its scheduled runs 0.4 seconds from
    their occurrence (565 ms the longest), its worker at 5 to 22% of a processor. TF-122 AC1 asks for under a minute.
    With two worker processes for five minutes (`tools/measurements/job-workers.sh echo`), the 31 occurrences of seven
    scheduled jobs each ran once while both workers held runs; and after a worker's restart, the runs it had been
    running were taken by the new process 16 to 17 seconds after the stop, the time it took to start (decision 4).
11. **A suite's jobs run only where it is installed (TF-244, ADR-0059).** A suite's module declares its jobs as the
    Foundation's do; a unit that does not run the suite never declares them. When a suite is removed, its queued runs are
    held at the worker's next start (state `held`, no alert, no dead letter); they resume by themselves when the suite is
    added again, and go with the suite's data when that is deleted.
12. **A run's trace and its tenant.** Every run is a span of `Iron.Jobs`, with the tenant's identifier, the job, the run,
    the attempt and the class. A run started by a request keeps the request's trace context and its span links to it
    (TF-130 AC3). The run's database connection is the unit's, which holds no other tenant's, and its telemetry carries
    the unit's tenant (TF-003 AC2). The two exception rows are removed.
13. **The console, the operator, and one view of the dead letters.** `jobs.jobs.read` (Medium) reads the jobs, their runs
    and logs; `jobs.jobs.manage` (High) pauses, resumes, starts a job now and runs a dead letter again, each with a
    reason and a recent sign-in, audited (`jobs.job_paused`, `jobs.job_resumed`, `jobs.run_triggered`, `jobs.run_rerun`,
    administrative). Without the permission it is refused (TF-121 AC3). A paused job queues nothing and its queued runs
    are not claimed until it is resumed (TF-121 AC2). The shell's Jobs page and Dead letters page are the console's; the
    Dead letters page lists the dead letters of jobs and, for a person who may read events, of event subscriptions,
    newest first, each run again or replayed from there. The operator does the same for a tenant through the control
    plane: `make jobs`, `make job-run`, `make job-pause`, `make job-resume` and `make job-rerun` ask for a fleet job of kind
    `jobs`, which runs the unit's `jobs` command as the owner role with what was asked; both trails keep the operator's
    name and reason (`tenancy.jobs_listed`, `tenancy.jobs_action_requested`, and the unit's events above).
14. **The control plane runs no job runner yet.** Its loops (its own chain's sealing, its alert rules and the copy to
    Alertmanager, the fleet's provisioning, key rotations and chain verifications, the incidents' escalation, the
    reconcilers, break-glass) stay hosted services: they serve one plane, not a tenant, and S6's operator console is
    where they are steered. The control plane's job console comes with it.
15. **What TF-124 names and T2 does not ship has its home.** Notification delivery comes with the notifications (S4 T4),
    webhook delivery with the webhooks (S4 T9), the file quarantine and orphan purge with the files' lifecycle (S4 T3,
    TF-124 AC2), metering with billing (S6), and backup verification with the first Azure deployment (TF-124 AC3). Each
    will be a job declared as above; the exceptions are in `docs/trace/exceptions.md` with their homes.

## Consequences

- A new background task is a job declaration and a handler, with no hosted service and no timer of its own; the console,
  the retries, the dead letters, the alerts and the operator's commands come with it.
- Two worker processes share a unit's jobs safely, so a unit can run a second worker for capacity (S9) without change.
- Every unit's database has `kernel_jobs` with its jobs' records and runs; `jobs.prune_runs` keeps its size bounded.
- A frequent loop's latency is its pass interval, as before; its run is a minute long, so the console shows one run a
  minute for it, with its passes in the log.
- The control plane's loops are not yet in a job console (decision 14).

## Alternatives considered

- **Quartz.NET with its clustered store.** Proven and complete as a scheduler, but its own schema and connection in
  every tenant's database, and the history, log, dead letters, audit, priorities and console still ours to build
  (decision 3).
- **Hangfire.** A complete product with a dashboard, but a second console beside the shell's, its own storage model,
  and the LGPL for the core with a third party's PostgreSQL storage.
- **A run row for every pass of a frequent loop.** Simple, and 300,000 rows a day for the dispatch alone (decision 8).
- **The process caches as unit jobs.** One worker refreshing would leave every other process stale (decision 9).
