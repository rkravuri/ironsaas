# ADR-0056: Security monitoring over the audit trail, the operator's alerting, incidents with the 72-hour notice, and the lost spans

**Status:** Proposed
**Date:** 2026-10-29
**Decided by:** Product owner and architect, in the brief of S3 T10b (rules over the audit trail, a failing rule as an alert); the details here are the task's, for the review

## Context

TF-079 asks that authentication failures, lockouts, permission denials, impersonations, break-glass, mass exports and
configuration changes feed security alerts with thresholds, that a misconfigured rule be reported as failing rather
than skipped (AC4), and for an incident runbook with severities, roles, communication, the post-incident review (AC2)
and the duty to tell a tenant about a breach of personal data within 72 hours. TF-130 AC4 asks that requests complete
while the trace exporter is unavailable, and that the spans it buffers and then drops be counted.

The audit trail already held almost every event the rules need, with the principal (TF-060, ADR-0013, ADR-0050). Some
did not record what a rule groups or weighs by: no refusal kept the client's address, only the audit export kept its
rows, two exports wrote no event, six security event kinds were never declared, and the seed changed system roles
without a record. The S2 plan's later item, a system role's new permissions raised as a privilege grant, was folded
into this task.

## Decision

### Rules over the audit trail, not over metrics

- **A kernel module, `monitoring`, counts audit events in sliding windows** through `IAuditCounts` (Audit's contract):
  the events of the rule's types, with an outcome, a detail or a target it may require, grouped by principal, client
  address, target (a login identifier, a tenant), record, or the whole trail. A rule may count only events whose detail
  holds a number above a limit (an export of more than 10,000 rows) or add a detail up (the reports a summary stands for).
- **Why not metrics**: grouping by person in Prometheus would make a label of every person, which it handles badly, and
  the audit trail already holds the principal, the time and the details, for as long as the trail is kept.
- **What the trail now records for them**: a refusal (outcome `denied`) and any event written before sign-in carry the
  client's address in their details, added by the writer; the roles' and the acceptances' exports write their events
  with `rows`; role events touching a critical permission carry `critical`; the CSP reports of a minute become one
  summary event at low severity, so anonymous reports cannot fill the trail; the six undeclared security kinds are
  declared. The audit search filters by `target` and by `address`, so every alert's events can be found.
- **The rules ship in code** (`AlertRules`), each with its types, grouping, threshold, window, cool-down, severity,
  scope (unit, control plane or both) and the runbook's section: denials (100 by one principal in 10 minutes), failed
  sign-ins by address and by login identifier, lockouts by both, impersonation, break-glass opened or signed in, an
  emergency account's sign-in, exports of more than 10,000 rows, changes in critical areas (roles with critical
  permissions, single sign-on, keys, retention, legal holds, the MFA policy, the rules themselves), rate-limit refusals
  and CSP reports; in the control plane, break-glass requests, the operator's impersonation, broken chains and disputed
  anchors, failed key rotations, and overdue incident reviews and notices.

### Every minute, one burst one alert, no event counted twice

- **The unit's worker runs the rules every minute** (`Monitoring:EvaluationSeconds`, 60), one pass at a time per
  database (an advisory lock). A window ends a few seconds before now (`SettleSeconds`, 5), so a transaction still
  committing is counted at the next pass.
- **A group at or above the threshold raises an alert**; within the rule's cool-down the alert raised for the same rule
  and group counts the burst on rather than raising another, unless it was resolved. A later count starts after the last
  event the previous alert saw, so no event is counted by two alerts. A rule with a threshold of one raises an alert for
  each new event, and after a gap it looks back to where it last counted, within a day.
- **The alert records** the rule, its group, the count, the threshold and window it ran with, the first and last event,
  and two links: the shell's audit search (`/audit?event_type=...&actor_id=...&since=...&until=...`) and the same search
  on the API. Raising, acknowledging and resolving (with a note) are audit events.

### The operator's values, a tenant's only stricter

- **The operator sets the values** in the unit secret's configuration, `Monitoring:Rules:<rule>:Threshold`,
  `WindowMinutes`, `CooldownMinutes`, `Above`, `Severity`, and every tenant gets them.
- **A tenant may make a rule stricter, never looser**: a lower threshold, a longer window (up to a day), a lower limit of
  rows; a rule that raises an alert for each event has nothing to tighten. A looser value is refused with the bound.
  A tenant's value is applied only where it is still stricter, so an operator who tightens a default later tightens
  every tenant with it. There is no switch to turn a rule off.

### A rule that cannot run is an alert

- **Failing, never skipped** (TF-079 AC4): a value that is no whole number or out of bounds, a key or rule name under
  `Monitoring:Rules` that is none, an unknown severity, an event type no module of the host declares, or an evaluation
  that throws makes the rule `failing` with the reason, which the rules page shows; the first time, it raises an alert
  of its own, at high severity, leading to the rule. The other rules run on. Corrected, the rule runs again and says so.
- **A gap between passes is reported**: a pass more than 10 minutes after the last (`LateAfterMinutes`) raises one
  alert saying when the rules did not run.
- A host test checks that every rule of a unit and of the control plane counts types its modules declare, so no rule
  fails on the day it ships; every rule names a section of the runbook, which a test checks exists (TF-209 AC3, AC4).

### The control plane and the operator's alerting

- **A unit copies each alert to the control plane** after every change, as its own client, through Tenancy's
  `PUT /api/v1/alert-copies/{id}` (`tenancy.alert_copies.append`, the unit-client role's); the tenant is the client's
  binding, never the request's, and an older revision changes nothing. A person is named by their id and a login
  identifier masked: the person's details stay in the tenant.
- **The control plane runs the same module over its own trail** and keeps its alerts and the copies together.
- **Every alert goes to Alertmanager** through its v2 API, every 30 seconds: an open alert is sent at each pass so it
  stays firing; an acknowledged or resolved one is sent once with its end, so the page stops. Labels are what routing
  needs (the rule, the tenant, the severity), annotations what the person paged reads (the counts, the link, the runbook).
- **Why Alertmanager, posted to by the control plane**: it is the alerting Prometheus will use for TF-134's rules, so
  the operator has one place for both; posting from the control plane keeps the units free of any route to the
  operator's systems; in Azure the same Alertmanager receives the same posts and only its receivers change. The laptop
  runs `prom/alertmanager:v0.34.1` in the `telemetry` and `ci` profiles, limited to 64 MB, with a receiver that pages
  nobody; Prometheus is pointed at it already. A Prometheus rule over a metric that counts the open alerts was the
  other way: it would page per rule, not per alert, and lose the link to the events.

### Incidents and the 72-hour notice

- **Incidents live in the control plane** (Tenancy, `tenancy.incidents.manage`): an operator declares one with a
  severity from 1 to 4, the tenants affected, a lead (and the communications and scribe roles) and a summary. Its
  timeline keeps each entry, its communication log what was said, and each step is an audit event of the control plane.
- **A personal data breach** is recorded per affected tenant, with when it became known (which the operator may give,
  up to 30 days back, the moment the duty runs from); the deadline to tell the tenant is 72 hours after it, the notice
  sent is recorded against the tenant, on time or late, and the incident does not close while a tenant is untold.
- **The review** (what happened, why, what changes) is due within 10 days of the close. A pass every five minutes
  escalates once a review not recorded by its due time and a tenant not told by its deadline: an entry, a high-severity
  event that the control plane's rules raise to the operator's alerting, and an `ALERT:` line.
- The runbook is `docs/security/incident-response.md`: severities with examples, roles, communication with templates,
  the notice, evidence to keep, the review, and what to do first for each rule. `make incident-declare`,
  `incident-note`, `incident-close`, `incident-review` and `incident-show` drive it on the laptop.

### The lost spans (TF-130 AC4)

- **Spans leave through a processor of our own**, `CountedBatchExportProcessor`, in front of the OTLP trace exporter.
  Ending a span only puts it on a bounded queue (`Telemetry:Traces:QueueSize`, 2048) and never waits; a thread of the
  processor's own sends it in batches.
- **Every span lost is counted**: a batch whose export fails or times out, and a span that finds the queue full, add to
  `telemetry.spans_dropped` with the reason (`export_failed`, `queue_full`); `telemetry.spans_exported` counts the rest
  and `telemetry.spans_queued` shows the buffer. The counters are cumulative, so Prometheus shows the loss once the
  collector is back.
- **Why not the SDK's batch processor**: it behaves the same way but keeps its count internal. **Why each signal has its
  exporter**: `UseOtlpExporter` cannot be mixed with a processor of our own for traces, so metrics and logs take the
  SDK's exporters, read from the same variables.

## Options considered

- Counting per principal in metrics and alerting in Prometheus: labels by person, and no link to the events. Not chosen.
- Rules a tenant defines: more reach, and a way to write a rule that never fires. Not now; the tenant tightens the
  kernel's rules.
- A tenant turning a rule off: a silence an attacker with an administrator's access would use first. Not chosen.
- Each unit posting to Alertmanager: every unit would need a route and credentials to the operator's systems. Not chosen.
- Incidents in a module of their own: they exist only in the control plane, whose module is Tenancy; a module there
  would need its own schema and roles for three tables. Not chosen.
- The SDK's batch processor, read by reflection: its count is internal and may change in any release. Not chosen.
- A fake OTLP receiver in the tests: it would not prove what the exporter does against the collector the units use; the
  test starts the pinned collector, pauses it (a collector that hangs) and stops it.

## Consequences

- A tenant sees, within two minutes, what the trail shows of an attack or a mistake, and the operator is paged the same
  way for every tenant; a broken rule is as visible as a firing one.
- The trail records a little more: the address on refusals and on what happens before sign-in, and a role event's mark.
- Alerts of each event's rules may be several a minute in a busy tenant; the cool-down and the grouping keep them to one
  per burst and group.
- A down or hanging collector costs spans, never requests, and the loss is visible.
- Later: alert rules over metrics, dashboards and the on-call channel with its fallback (TF-134, S6); the operator's
  console pages for incidents and alerts (the API and the make targets exist now).
