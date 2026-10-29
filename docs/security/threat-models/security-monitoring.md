# Threat model: security monitoring and the operator's alerting

- **Modules:** monitoring
- **Endpoints:** none
- **Reviewed:** 2026-10-29 (S3 T10b)

## What it protects

The tenant's and the operator's sight of an attack: the alert rules over the audit trail, their thresholds and state,
the alerts they raise with their links to the events, the copies of a unit's alerts in the control plane, and the way
to the operator's alerting (Alertmanager). An attacker wants the rules silenced, loosened or failing unseen, an alert
resolved before anyone reads it, or the person's details carried out of the tenant through the copies.

## Who can reach it

A tenant's administrators, with `monitoring.alerts.read`, `monitoring.alerts.manage` and `monitoring.rules.manage`; the
unit's worker, which runs the rules and sends the copies as the unit's own client; the control plane's operators; the
control plane, which posts to Alertmanager on the operator's network.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | Another unit, or anyone, sending alerts as a tenant's unit. | Copies go through Tenancy's endpoint, which takes the tenant from the unit client's binding, never from the request (`tenancy.alert_copies.append`, the unit-client role's). | TF-079 |
| Tampering | A tenant administrator turning a rule off or loosening it before an attack. | A tenant's value is applied only where it is stricter than the operator's; there is no way to switch a rule off; every change is audited and is itself counted by the rule for critical configuration changes. | TF-079 |
| Tampering | An alert resolved to hide it. | Resolving needs `monitoring.alerts.manage` and a note, and is audited; the control plane's copy and Alertmanager keep the alert's history; the events stay in the trail. | TF-079, TF-060 |
| Repudiation | Denying that a burst was seen or handled. | Raising, acknowledging and resolving are audit events, with the person; the alert keeps the counts and the window. | TF-060, TF-079 |
| Information disclosure | A person's login identifier carried to the control plane and the pager. | A copy names a person by their id and masks a login identifier to its first letter and domain; the person's details stay in the tenant. | TF-069 |
| Denial of service | A flood of events producing a flood of alerts. | One alert per rule and group within the cool-down, which counts the burst on; a rule for each event raises one alert per pass and group; rules read windows of minutes through the trail's indexes. | TF-079 |
| Denial of service | Monitoring stopped without anyone knowing. | A rule that cannot run is failing and raises an alert; a pass after a gap of more than 10 minutes raises one; Alertmanager and copy failures are counted and logged as `ALERT:` lines. | TF-079 AC4 |
| Elevation of privilege | The rules page used to change what the operator set. | The operator's values come from the unit secret's configuration, which no endpoint writes. | TF-079 |

## What remains

- One alert per pass for a rule that raises an alert for each event: several impersonations by one person within a
  minute are one alert with their count.
- The laptop's Alertmanager has no receiver that pages anyone; in Azure the receiver is the on-call channel (TF-134,
  S6), with its fallback.
- Alert rules over metrics (saturation, error rates) are TF-134's, in S6.
