# Threat model: the eProcurement Suite's Purchasing module and the suite's installation

- **Modules:** purchasing
- **Endpoints:** none
- **Reviewed:** 2026-10-30 (V2)

## What it protects

What the tenant has of the eProcurement Suite: the record of the suite's installation in its database (when, by which
fleet job and with which release it was first migrated and seeded, and the latest time an update did it again) and the
About page that shows it with the plan and the entitlements of the suite's modules. Today the module holds no business
data; PR0 brings purchasing groups and purchasing profiles. A suite's modules exist only in the units of tenants that
bought it (TF-244, ADR-0059).

## Who can reach it

Only units whose configuration names the suite (`Suites:Installed`), which the fleet writes from the control plane's
record of the tenant's suites, changed by an operator with `tenancy.suites.manage` and a reason, audited. In such a unit,
signed-in people with `purchasing.suite.read` (the tenant administrators and the read-only roles, by the seeds), and only
while the tenant's plan for the suite licenses the module; the endpoint is behind the entitlement check like every
licensed module's (ADR-0034).

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | A unit claiming to have the suite, or a person reaching the suite's endpoint in a unit that does not have it. | The unit's suites come from its environment, which the fleet renders from the control plane's record and the unit cannot write; a unit without the suite loads none of its modules, so the endpoint answers not found there; the endpoint needs a signed-in person with its permission. | TF-244 AC2, TF-210, TF-033 |
| Tampering | Changing the record of the installation, or the tenant's suites. | Only the seed writes the installation row, as the system, audited in the same transaction; no endpoint writes it. Adding or removing a suite is the operator's, with a reason, audited in the control plane, and carried out by the fleet's update job. | TF-244 AC3, AC4, TF-060 |
| Repudiation | Denying that a suite was installed, removed or seeded again. | The control plane audits each addition and removal with the operator and the reason; the unit audits each installation and seed with the job and the release; the fleet keeps every job's steps. | TF-244 AC3, TF-060 |
| Information disclosure | A tenant without the suite learning about it, or a person without the permission reading what the tenant has. | A unit without the suite has none of its schema, endpoints or shell modules; `/me/context` lists only the suites the unit runs; the endpoint needs `purchasing.suite.read` and the module's entitlement. | TF-244 AC1, AC2, TF-040 |
| Denial of service | Removing a suite losing its data, or a unit that cannot start because of its suites. | Removal switches the modules off and keeps the schema, its rows and files; adding the suite again finds them. A unit whose configuration names a suite its image does not carry stops at start with a message naming it, before it serves anything. | TF-244 AC4 |
| Elevation of privilege | A plan of one suite granting another suite's or the Foundation's module, or a suite's permission widening a role elsewhere. | A plan names only its own group's modules and features, refused otherwise when saved or applied; a suite's permissions exist only in the units that run it, and the seed takes them out of the system roles when the suite is removed. | TF-244 AC7, TF-030 |

## What remains

- A tenant role that named the suite's permission keeps the name after the suite is removed; it grants nothing while the
  unit does not run the suite, and works again when the suite is added back.
- The About endpoint is behind the module's entitlement, so a tenant whose plan for the suite expired sees the suite's
  state on the console's Subscription page instead.
