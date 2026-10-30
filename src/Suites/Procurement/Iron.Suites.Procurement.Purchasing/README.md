# Iron.Suites.Procurement.Purchasing

The eProcurement Suite's shared master (ADR-0043, ADR-0058), module 1 of the suite's list. Layer Suite, suite Procurement, schema `procurement_purchasing`, licence `purchasing` (sold with the suite, `platform`). Requirements: `docs/prd/suites/procurement/purchasing-PRD.md`; PR0 fills it with purchasing groups and the purchasing profiles.

Today it is the suite's skeleton (V2, TF-244, ADR-0059): it holds no business data, records the suite's installation in the tenant, and shows what the tenant has of the suite on an About page. A unit loads it only when its tenant has the suite.

## The suite's definition

`PurchasingModule.Suite()` is the eProcurement Suite as the hosts compose it: short name `procurement`, its name and description (`Contracts/PurchasingContracts.cs`, `ProcurementSuite`), its modules in order (Purchasing first), and its starter plan, eProcurement Essentials (`procurement-essentials`, 30 days of grace, Purchasing). The definition lives here while the suite has one module; with the products it moves to where the hosts compose the suite, since a master never references a product.

## The installation record

One table, `procurement_purchasing.installations`, one row per suite (`SuiteInstallation`, tenant-wide): when the suite was first installed, by which fleet job and which release, and the latest seed's time, job and release. The seeder (`SuiteInstallationSeeder`, order 1000, after the Foundation's) reads the job from `Seed:JobId`, which the fleet's seed step sets, and the release from `Release:Version`. The first seed inserts the row and audits `purchasing.suite_installed`; every later one, on an update or when the suite is added again, updates the latest seed and audits `purchasing.suite_seeded`, and keeps the first installation. Removing the suite drops nothing: the row is there when it comes back.

## The About page

`GET /api/v1/suites/procurement/about`, permission `purchasing.suite.read` (low risk, seeded to the tenant administrator), behind the module's entitlement like every licensed endpoint: the suite, its name and description, the release the unit runs, the installation, the plan the tenant holds for the suite with its grace days, whether the subscription is stale, and every module and feature of the suite with its status, reason and dates (`ISuiteSubscriptions` from the Entitlements module). In a unit without the suite the route does not exist and answers 404.

The shell's page is `shell/src/modules/suites/procurement/purchasing/`, at `/procurement/about`, on the console template, read only.

## Tests

- `tests/Suites/Procurement/Iron.Suites.Procurement.Purchasing.Tests`: the schema only where the unit runs the suite, the seed's record and its audit, the schema and rows kept when the suite is removed and found when it is added again, the About endpoint and the context with and without the suite, and the suite's module switched off by its own plan's grace.
- `tests/Iron.Host.Tests`: the unit's composition with and without the suite, the endpoint's 404 without it, and the seeds per composition.

## Threat model

`docs/security/threat-models/purchasing.md`.
