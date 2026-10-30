# Threat model: sample records for testing a running unit

- **Modules:** samples
- **Endpoints:** none
- **Reviewed:** 2026-10-29 (V1)

## What it protects

Nothing of a customer's: sample records have a name and nothing else. The module lets the smoke and the browser tests
meet the kernel's record machinery in a running unit (its policy, sharing, visibility tags and history) before any
product brings records (ADR-0057). What it must not do is reach a customer's unit, or give anyone there a way in.

## Who can reach it

Only units the operator marked for testing (`tenancy.tenants.manage`, audited): the fleet writes `Samples:Enabled` into
such a unit's secret, and the API host loads the module only then. In such a unit, signed-in people with
`samples.records.read` or `samples.records.manage`, which only exist there.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | A unit claiming to run the module. | The switch is in the unit secret, which the fleet writes from the control plane's tenant record and the unit cannot write. | TF-210, TF-003 |
| Tampering | Changing a sample record outside one's units. | Every route opens the record through its policy; changing, sharing and tagging need `samples.records.manage` over the record's unit; rule 5 holds for its tags. | TF-035, TF-241 AC5 |
| Repudiation | Denying a change of a sample record. | Sample records are tracked: every save is an audit event of their field history; marking a tenant is audited in the control plane. | TF-062 |
| Information disclosure | The module, or its permissions, appearing in a customer's unit. | Loaded only where the unit secret switches it on; never in the licensing catalogue; its schema is made only where it is loaded; the installation record checks a marked unit against the seeds with the module and every other unit against those without. | TF-218 |
| Denial of service | Sample records filling a unit. | Only marked units have them; the smoke's runs make three records each, in a unit it deactivates. | none |
| Elevation of privilege | Its permissions widening a role elsewhere. | Its permissions exist only in a marked unit's catalogue; no other unit's role can name them. | TF-030 |

## What remains

- A marked unit's tenant administrators hold the module's permissions, as they hold every permission of their unit;
  the module holds nothing but names.
