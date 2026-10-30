# ADR-0059: Suites per tenant: the eProcurement Suite's skeleton, a plan per suite, and what a unit loads

**Status:** Accepted
**Date:** 2026-10-30
**Decided by:** Product owner and architect; the owner chose Option B on 2026-10-29, in the brief of V2; accepted at the design session's review of V2 (2026-10-31)

## Context

TF-244: a customer gets the suites it buys. The Foundation is installed for every tenant; a suite's modules are
migrated, seeded and loaded only in the units of the tenants that bought it. Adding a suite to an active tenant is an
update job; removing one switches its modules off and keeps its schemas, rows and files, so adding it again finds
them. Inside a suite, modules and features are entitlements (TF-040 to TF-046, ADR-0034), with plans per suite beside
the Foundation plan every tenant holds; a plan of one suite never names another group's module or feature.

ADR-0043 set the layers (Kernel and Business are the Foundation; a suite is the third) and one image per release for
the Foundation and every suite. ADR-0058 listed the eProcurement Suite's modules, Purchasing first, its shared master.
Before V2 every unit loaded every module it carried, the catalogue grouped what is licensable by suite, and a plan was a
list of codes with no group.

## Decision

1. **Option B: the suite is declared for real now, with a skeleton module.** `Iron.Suites.Procurement.Purchasing`
   (layer Suite, suite Procurement, schema `procurement_purchasing`) holds no business data yet: one table,
   `installations`, records the suite's installation in the tenant (installed at, by which job, which release, and the
   latest seed's), and one page, "About this suite", shows what the tenant has of the suite and under which plan
   (`GET /api/v1/suites/procurement/about`, permission `purchasing.suite.read`, seeded to the tenant administrator).
   So the laptop and the pipeline prove a suite's installation end to end in the slice about exactly that, not inside
   the first business slice. PR0 fills the module with purchasing groups and profiles.
2. **A suite is declared in code.** `SuiteDefinition` (in `Iron.Kernel.Api`, which names no suite) holds the short
   name, the name and description for people, the suite's modules in order, the shared master first, and its starter
   plans. The hosts list the suites their image carries (`UnitModules.Suites()`); one image carries the Foundation and
   every suite. The kernel and the business layer never reference a suite; the architecture tests still hold it.
3. **What a unit loads at start.** The unit's configuration names its suites in `Suites:Installed` (a list of short
   names). The host registers the Foundation's modules and those suites' modules, and nothing else: a suite the unit
   does not run has no endpoints (its routes answer not found), no migrations, no seeds, no entry in the unit's
   licensing catalogue (so `/me/context` lists none of its modules) and no group on the home page. A suite the image
   does not carry stops the unit at start, naming it; a module of a suite not named is refused by `AddModules`. The
   fleet sets `Suites__Installed` in the unit's environment, not in its secret: a change of the tenant's suites then
   changes the containers' environment, so the update job's `docker compose up` recreates them, and a unit runs exactly
   the suites the control plane records. The image commands (migrate, seed) get the same variable.
4. **Plans belong to a group.** A plan is a Foundation plan (`foundation`) or a plan of one suite (its short name).
   Standard and Enterprise are Foundation plans. A suite declares its starter plans in code; the catalogue file carries
   them in the suite's group, and the control plane's seed creates each once, after which it is the operator's. The
   eProcurement Suite's is Essentials (`procurement-essentials`, 30 days of grace, Purchasing). A plan names only its
   own group's modules and features: one that names another suite's or the Foundation's is refused when it is saved
   and again when it is applied (a plan saved by an older build), each problem naming the code and the group it belongs
   to (TF-244 AC7). A plan keeps its group; a plan for another group is a new plan.
5. **How the plans combine.** A tenant holds one Foundation plan (on the tenant, with its grace days) and, per suite it
   has, one suite plan (a `tenant_suites` row with the plan and its grace days). Every entitlement row carries its
   group. Applying a plan replaces only its own group's rows, with the history and the audit event as before; one
   revision counts every change of any group. The store's document is the union of all the rows, each entry with its
   group and its own plan's grace days, and lists the suites with their plans. So the Foundation's features apply
   whatever suites the tenant has, and a suite plan's end date and grace are its own. A document written before this
   decision has no group or grace on its entries, which then follow the document's grace days.
6. **Installing a suite.** The control plane records each tenant's suites. A tenant is created with its suites and
   their plans in one transaction, and the provisioning job installs them. Adding a suite to an active tenant
   (`POST /api/v1/tenants/{slug}/suites`, `make suite-add`, permission `tenancy.suites.manage`, critical) applies the
   suite's plan (the one named, or the suite's only plan), writes the history and the audit event `tenancy.suite_added`
   with the operator and the reason, and asks for an update job, in one transaction: if a job is already open, nothing
   changes. The update job writes the unit's environment, migrates and seeds (the suite's seed records the installation
   with the job and the release, audited `purchasing.suite_installed`), then recreates the unit with the suite.
7. **Removing a suite.** `POST /api/v1/tenants/{slug}/suites/{suite}/remove` (`make suite-remove`) removes the suite's
   row and its group's entitlements, with the history, the audit event `tenancy.suite_removed` and the reason, and asks
   for an update job, in one transaction. The store's copy follows the commit, so within a minute the unit refuses the
   suite's modules, as for any entitlement; the update job then recreates the unit without the suite, whose routes are
   gone. Nothing drops a schema or deletes a row or a file. Added again, migrate finds the schema at its version, the
   seed updates the installation row and keeps the first installation, and the records are where they were.
8. **Expiry is not removal.** A suite plan's end date and grace work as A4's: past its grace the suite's modules are
   switched off within a minute and their data hidden; the suite stays installed until the operator removes it.
9. **The installation record names the tenant's suites** (ADR-0052). It reads them from the unit's API container's
   environment, which is what the unit read at start, not from the schemas, because a removed suite keeps its schema.
   The API image states the seeds' hash of every composition a unit can run (the Foundation with each set of its
   suites, with and without the sample module), and the record checks the tenant's seeds against its composition. A
   removed suite's schema is reported as kept, not checked against the repository's migrations.
10. **The shell follows the unit.** `/me/context` names the suites the unit runs with their plans; the home page shows
    a suite's group only where the unit runs the suite and the person is entitled to one of its modules.

## Options considered

- Option A, no suite module until PR0 (a test-only module to prove installation): rejected by the owner, since
  installation's problems would then surface inside the first business slice.
- Option B, the suite declared now with a skeleton module and a real About page: chosen.
- The suites in the unit's secret: rejected, since Compose recreates a container only when its own configuration
  changes, so a unit would keep running its old suites after the update.
- The suites derived from the entitlements (a tenant has a suite when a plan names its module): rejected, since
  installing is an operation with its own job and audit, and an expired plan must switch a suite off, not uninstall it.
- One plan naming every group's codes: rejected, since a suite could not be sold on its own beside the Foundation.
- Grace days on the document only: rejected, since each plan has its own; kept as the fallback for older documents.
- An image per suite: left for later (ADR-0043); one image keeps one release, one pipeline and one installation record.
- The installation record reading suites from schema prefixes: rejected, since a removed suite keeps its schema.

## Consequences

- A suite's module is written once and runs only where it was bought. A later suite is a `SuiteDefinition`, a line in
  the hosts' list and `make catalogue`; its plans are declared beside it.
- The seeds' hashes grow with the subsets of the carried suites (two now, four with a second suite, eight with a third).
  Revisit when a third suite arrives: hash per suite and combine, rather than per subset.
- While the suite has one module, its definition lives in the Purchasing module. When products arrive it moves to where
  the hosts compose the suite, since a shared master never references a product.
- A removed suite's permissions leave the system roles at the next seed. A custom role keeps the name, which grants
  nothing while no endpoint of the unit asks for it, and works again when the suite is added back; while the suite is
  away, saving that role is refused for a permission the tenant does not know, until the administrator removes it.
- A removed suite's data stays without end. Deleting it, with the tenant's offboarding, is a later decision.
- An add or remove while another job is open is refused and changes nothing; the operator asks again when it ends.
