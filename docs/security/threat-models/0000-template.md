# Threat model: the module or interface

Copy this file to a name of its own before the module or the external interface is built (TF-077 AC2, ADR-0055).
The three lines below are what the check reads: every module, by the name its `IModule` declares, and every anonymous
endpoint, as `METHOD /path`, must be named by one record. Keep it to a page; say what is true of the code, and name the
requirements whose tests prove each control.

- **Modules:** the module names, or none
- **Endpoints:** the anonymous endpoints, or none
- **Reviewed:** the date (the task)

## What it protects

The data, the credentials and the decisions an attacker would want, in a few lines.

## Who can reach it

Who calls it, from where, with what proof of who they are: anyone on the network, a signed-in person with a permission,
an operator, a unit, the fleet.

## Threats

One row per category at least; more where the interface has more.

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | Someone passes for someone else. | | |
| Tampering | Someone changes data or code they should not. | | |
| Repudiation | Someone denies what they did. | | |
| Information disclosure | Someone reads what they should not. | | |
| Denial of service | Someone stops others from using it. | | |
| Elevation of privilege | Someone gains a right they were not given. | | |

## What remains

The risks accepted for now, with where they are tracked: a later task, a later item in STATUS, an exception in
`security/exceptions.yaml` or `docs/security/exceptions.md`, or a finding in `docs/security/findings.md`.
