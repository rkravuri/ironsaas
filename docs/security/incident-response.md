# Incident response

The runbook for the platform's operators when something threatens a tenant's data, its records or its access (TF-079,
ADR-0056). It says how severe an incident is, who does what, how to speak inside and to the tenants, how to meet the
duty to tell a tenant about a breach of personal data within 72 hours, what evidence to keep, and how the incident ends
with its review. Each security alert points to its section below, by the rule that raised it.

Everything an operator does here is recorded: the incident's timeline in the control plane and an audit event of the
control plane for each step. On the laptop the make targets drive it; in Azure the same control plane API does.

## Severities

| Severity | What it is | Examples | Respond | Updates |
|---|---|---|---|---|
| 1, critical | Harm is happening or has happened to tenants' data or records. | Personal data reached someone it should not; a tenant's records changed without trace; an audit chain broken; an attacker holding an operator's access. | At once, everyone on call. | Every hour inside; tenants as soon as the facts allow. |
| 2, high | An attack in progress that is contained, or a control that failed where harm is likely. | A burst of failed sign-ins locking a tenant's people out; a break-glass access nobody can explain; an export far larger than the tenant's work needs. | Within one hour. | Every four hours. |
| 3, moderate | A security control not working, or activity that needs explaining, without harm seen. | An alert rule failing; the operator's alerting unreachable; a person denied many times by mistake or by probing. | The next working day. | Daily. |
| 4, low | One anomaly, or an alert that turns out to be expected. | The smoke test's own denials; a single lockout of a person who forgot a password. | Within the week. | At the close. |

When in doubt, choose the more severe and lower it later: the timeline records the change. An incident whose personal
data question is open is severity 2 at least until the answer is known.

## Roles

- **Incident lead.** Decides: the severity, what to contain, when to tell the tenants, when to close. Names the others.
  One lead at a time; a hand-over is a timeline entry.
- **Communications.** Speaks for the incident: inside, to the tenants' administrators and their data protection
  officers, and keeps the communication log (`KIND=communication`). Sends the 72-hour notices.
- **Scribe.** Keeps the timeline: what was seen, done and decided, with the time, as it happens (`make incident-note`).

At severities 3 and 4 one person may hold two roles. At severity 1 the lead is never also the scribe.

## Declaring and running an incident

1. Declare it as soon as an alert or a report looks like one; the incident can close quickly if it is not:
   `make incident-declare SEVERITY=2 SUMMARY="..." LEAD="..." COMMS="..." SCRIBE="..." TENANTS=echo,acme`.
   Add `PERSONAL_DATA=1` and `KNOWN_AT=2026-10-29T08:00:00Z` when tenants' personal data is affected, from the moment
   that became known (the deadline runs from then, not from the declaration).
2. Contain: stop the harm first (end sessions, revoke access, use the kill switches, rotate a key), then understand it.
   Each action is a timeline entry: `make incident-note ID=<id> TEXT="..."`.
3. Name each tenant found affected later, or found to have its personal data affected:
   `make incident-note ID=<id> TENANT=bravo PERSONAL_DATA=1 [KNOWN_AT=...]`.
4. Tell the tenants (see the communication and the 72-hour notice below), each notice recorded:
   `make incident-note ID=<id> KIND=notice TENANT=echo TEXT="the notice as sent"`.
5. Close when the harm is stopped and every tenant whose personal data was affected is told:
   `make incident-close ID=<id> SUMMARY="..."`. The control plane refuses to close an incident with a tenant untold.
6. Record the review within 10 days: `make incident-review ID=<id> WHAT="..." WHY="..." CHANGES="..."`.

`make incident-show ID=<id>` prints the incident, each tenant's deadline and its state, and the timeline;
`make incident-show` lists the incidents.

## Communication

**Inside.** One channel per incident, named after its id. The lead posts the severity, what is known, what is being
done and when the next update comes, at the cadence of the severities' table. Facts only; a guess says it is one.

**To the tenants.** Communications writes to each affected tenant's administrators, and to its data protection officer
when personal data is affected, from the operator's support address. Say what happened, what it means for them, what we
have done, what they should do, and when they will hear next. Never name another tenant, never send a person's data
in the message, and never promise what is not known. Each message is logged with `KIND=communication`, or `KIND=notice`
for the breach notice.

First message:

> We are investigating an incident that affects your organisation's use of the platform. At {time, UTC} we found
> {what was found}. We have {what was done}. You do not need to do anything now / Please {what they should do}. We will
> write again by {time}. Our reference is {incident id}.

Update:

> An update on incident {id}. Since our last message we have {what changed}. {What it means for them.} We will write
> again by {time}.

Close:

> Incident {id} is closed. In short: {what happened, why, what we changed}. The full review follows within 10 days.

## The 72-hour notice

When a tenant's personal data is affected, the tenant (the controller) must be told without undue delay, and the
contracts say within 72 hours of the moment it became known; the tenant then has its own duty towards its regulator
and the people concerned. The incident records, for each affected tenant, when that became known and the deadline,
and the timeline shows it; the notice sent is recorded against that tenant, on time or late.

- The deadline runs from when it became known that the tenant's personal data was affected, not from when the
  incident was declared or the tenant added: give `KNOWN_AT` when it was earlier.
- Tell what is known by the deadline, even if not everything is: a later notice adds the rest.
- A tenant not told when its 72 hours are up is escalated once, as a critical alert to the operator's alerting
  (`incident_notices_overdue`), and the timeline says so.

The notice holds:

> Notice of a personal data breach, incident {id}. On {when it became known, UTC} we became aware that personal data of
> your organisation was {accessed / disclosed / changed / lost}. What happened: {nature of the breach}. Whose data and
> how much: {the categories and the approximate number of people and records}. What it may mean: {likely consequences}.
> What we have done and will do: {measures taken and proposed}. Your contact for this incident: {name, address}.

## Evidence to keep

Keep, and never change, from the start:

- **The audit search behind each alert**: the alert's link opens the tenant's audit search over its events; export it
  to CSV from the audit page (the export is itself recorded).
- **The chain's verification**: `make verify-audit TENANT=x FULL=1` records a verification of the tenant's whole chain;
  the record and any finding stay in the control plane.
- **Logs and traces**: Loki keeps log lines 30 days and Tempo its traces for its retention; export what the incident
  needs while it is there.
- **The operator's alerting**: Alertmanager's alerts for the time (`make security-alerts`).
- **The incident's own timeline and communication log**, in the control plane, and its audit events.

Evidence packs (the tenant's compliance page) give a tenant's auditor the trail of a period with its chain.

## The post-incident review

Due within 10 days of the close; the control plane escalates a review not recorded by then once, as an alert
(`incident_reviews_overdue`). The review is blameless: it asks what in the system let the incident happen, not who.

- **What happened**: the timeline in short, with the times that mattered: when it began, when it was seen, when it was
  contained, when it ended.
- **Why**: the causes, asked until the answer is something we can change.
- **What changes**: each change with its owner and when it is done, in the backlog; a change that needs a decision
  gets an ADR.

## The alerts

Each alert names its rule; the rule's section says what it means and what to do first. A tenant sees its alerts on
the console's Security alerts page and its rules on Alert rules; the control plane holds a copy of each and sends every
alert to the operator's alerting.

### Permission denials

`permission_denials`: one principal refused 100 times within 10 minutes by default. A person probing what they may
do, a script or integration with too few permissions, or a stolen session trying its luck. Open the events: the targets
say what was tried. Ask the tenant whether the person or client is expected to do it. If not, end the person's
sessions or revoke the client, and declare an incident at severity 2.

### Sign-in failures and lockouts

`sign_in_failures_by_address`, `sign_in_failures_by_login`, `lockouts_by_address`, `lockouts_by_login`: many failed
sign-ins from one address or for one login identifier, or identifiers locked. Many identifiers from one address is
credential stuffing: block the address at the edge, and declare an incident if any sign-in from it succeeded. One
identifier locked is usually a person who forgot their password; many of one tenant's at once is an attack on it.

### Impersonation, break-glass and emergency accounts

`impersonations`, `break_glass`, `emergency_sign_ins`, and in the control plane `break_glass_requests` and
`operator_impersonations`: each one raises an alert at once. Each must match a reason someone can show: the ticket of a
break-glass request, the support case of an impersonation, the outage that made an emergency account necessary. One
nobody can explain is an incident at severity 1 until it is explained.

### Mass exports

`mass_exports`: an export of more than 10,000 records by default (the audit trail, the roles, the document
acceptances). Ask the tenant whether the person needed it. An export nobody expects is a possible breach of personal
data: declare an incident and answer the personal data question first.

### Critical configuration changes

`critical_configuration_changes`, and in the control plane `operator_access_changes`: a role that holds a critical
permission created, changed, given or taken; a company login connected, changed or removed; a key rotated or retired;
retention or a legal hold changed; the MFA policy changed; an alert rule changed. The seed's change of a system role,
as a new module's permissions join it, is one of these: it is expected after an update. Anything else must match a
change someone asked for.

### Rate limits and policy reports

`rate_limit_refusals` and `csp_reports`: requests from one address refused again and again, and many reports of the
content security policy. Refusals are the limits working; many from one address are worth blocking at the edge. Many
policy reports after a release are a page the policy breaks; without a release, someone may be injecting content.

### Audit chain and keys

`audit_chain_breaks` and `key_rotation_failures` in the control plane: a tenant's audit chain found broken or an anchor
disputed, or a key rotation that failed. A broken chain is severity 1 until the verification says otherwise: follow the
chain's runbook in `docs/guides/local-stack.md` (verify in full, compare with the anchors, accept a restore only with
its ticket). A failed rotation leaves the old key in use: read the error with `make key-status TENANT=x` and rotate again.

### A failing rule

A rule that cannot run raises an alert of its own, and the Alert rules page shows why: a value the operator set that is
no number or out of bounds, a misspelt key or rule name under `Monitoring:Rules`, an event type no module declares, or
an evaluation that failed. Correct the configuration in the unit secret and update the unit; the rule runs again at the
next pass and its state says so. "The alert rules did not run for a while" means the worker was down: the rules that
alert on each event looked back over the gap, the rules that count over a window did not, so read the audit trail of the
gap by hand.
