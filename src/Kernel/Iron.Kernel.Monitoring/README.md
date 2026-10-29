# Iron.Kernel.Monitoring

Security monitoring (TF-079, ADR-0056). Kernel module (ADR-0029), placement: no business meaning. Schema
`kernel_monitoring`: `alerts`, `rule_values` (a tenant's stricter values), `rule_states` (how each rule last ran).

| Folder | Holds |
|---|---|
| `Contracts/` | `MonitoringPermissions`, `MonitoringAuditEvents`, `MonitoringErrors`, `MonitoringScope`, the alert's severities, states and kinds, `AlertCopy` and `IAlertCopies` (the control plane's store, which Tenancy hands the units' copies to) |
| `Data/` | `Alert`, `AlertRuleValue`, `AlertRuleState`, `MonitoringDbContext` |
| `Services/` | `AlertRules` (the rules the kernel ships), `AlertRuleResolver` (the operator's values, a tenant's stricter ones, and why a rule cannot run), `RuleEvaluator` (one pass), `AlertLinks`, `AlertService` (acknowledge, resolve, the rules page, a tenant's values), `AlertCopySender` (a unit's copies), `AlertCopyStore` and `AlertmanagerForwarder` (the control plane's), the workers |
| `Api/` | `/api/v1/alerts` (list, one, acknowledge, resolve) and `/api/v1/alert-rules` (list, `PUT /{name}`) |

The module runs in a unit and in the control plane: the host lists it with its scope (`new MonitoringModule()` in a
unit, `new MonitoringModule(MonitoringScope.ControlPlane)` there) and calls `AddMonitoring(scope)` where the rules run:
the unit's worker (with the copies to the control plane every 15 seconds) and the control plane's API (with the posts
to Alertmanager every 30 seconds).

## A rule

`new AlertRule(name, title, matches, grouping, threshold, windowMinutes, cooldownMinutes, severity, scopes, runbook)`
in `AlertRules.All`, with `QuantityKey`/`Above` to count only events whose detail is above a number, or `SumKey` to add
a detail up. Every event type it counts must be declared by a module of the host, or the rule fails, and its runbook
must be a section of `docs/security/incident-response.md`, or `RunbookTests` fails. A module that writes an event a rule
reads by its details uses the keys in `AuditDetails` (Audit's contracts): `address`, `rows`, `critical`, `reports`.

## Configuration

| Key | Default | What it does |
|---|---|---|
| `Monitoring:Rules:<rule>:Threshold`, `WindowMinutes`, `CooldownMinutes`, `Above`, `Severity` | the rule's | the operator's values for every tenant; a value out of bounds, or a key or rule that is none, makes the rule fail |
| `Monitoring:EvaluationSeconds` | 60 | how often the rules run |
| `Monitoring:SettleSeconds` | 5 | how long before now a window ends |
| `Monitoring:LateAfterMinutes` | 10 | a gap between passes longer than this is an alert |
| `Monitoring:CatchUpHours` | 24 | how far a rule for each event looks back after a gap |
| `Monitoring:CopySeconds` | 15 | how often a unit sends its changed alerts |
| `Monitoring:Alertmanager:Address`, `Seconds` | none, 30 | the control plane's alerting; empty, the alerts stay in the control plane |

Meter `Iron.Monitoring`: `monitoring.alerts_raised` (rule, kind), `monitoring.rules_failing`, `monitoring.copy_failures`,
`monitoring.alertmanager_failures`. An alert raised, a rule failing and a send that failed are also `ALERT:` lines.

Tests: `tests/Kernel/Iron.Kernel.Monitoring.Tests` (a database per test; the control plane and the event types of the
other modules stand in) and `MonitoringHostTests` in `tests/Iron.Host.Tests`.

Model change: `make ef-add MODULE=Monitoring NAME=<WhatChanged>`, then `make format`.
