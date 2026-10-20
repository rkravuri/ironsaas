# L2. IronSaaS Foundation and the suites built on it

Written on 2026-10-20 in the design session (ADR-0023). The owner's structure: **IronSaaS Foundation** is the platform, and any SaaS product can be built on it. The **eProcurement Suite** is the first suite built on it. Other suites, such as a School ERP, can follow on the same Foundation. A customer buys suites; inside a suite, modules and features are entitlements.

ADR-0029 has three layers: kernel, business, products. This task keeps the first two as the Foundation and turns the third into suites, while the Products layer is still empty and the change costs almost nothing. It also restructures the README for these names. Code that installs a suite per purchase is written as a requirement now (TF-244) and built before the first suite slice.

## Opening message

```
Read CLAUDE.md, docs/STATUS.md, docs/adr/0023, 0029, 0034, 0041 and 0042, docs/prd/README.md, the
README, Directory.Build.props, tests/Iron.Architecture.Tests (the layer checks), the shell's
eslint.config.js, the Makefile's ef-add and catalogue targets, and src/Products/README.md. Then read
docs/prompts/L2-suites.md and follow it: branch slice/l2-suites from main, commit the prompt file
alone first, write a short plan into the session, and build without waiting for approval unless the
"When to stop" rule in CLAUDE.md applies. Done when: the third layer is Suite, with each suite in
its own folder, namespace and schema prefix, and the build refuses a reference from one suite to
another; ADR-0043 records the structure, deployment by purchase and the repository choice; TF-244 is
written; the requirement documents sit under foundation/ and suites/procurement/ with make prd-check
passing; the catalogue groups modules by suite; the README, CLAUDE.md and the PRDs use the names
IronSaaS Foundation and eProcurement Suite; make test, make shell-check, make shell-a11y and make
format are clean. Finish with the report and the list of Business PRD requirements that only the
eProcurement Suite would need, as a proposal. Then stop.
```

## Decisions already taken (record them in ADR-0043, "IronSaaS Foundation and suites")

1. **Names.** IronSaaS Foundation is the kernel layer and the business layer together. A suite is a set of products sold together, with the masters only its products share. The first suite is the eProcurement Suite, short name `procurement`. Later suites take their own short name (for example `school`). The code namespace stays `Iron.*`.
2. **Layers.** `Kernel` and `Business` stay as they are (ADR-0029). The third layer is `Suite`, replacing `Products`:

   | Level | Project and namespace | Folder | Schema |
   |---|---|---|---|
   | A suite's shared master | `Iron.Suites.<Suite>.<Module>` | `src/Suites/<Suite>/Iron.Suites.<Suite>.<Module>/` | `<suite>_<module>`, for example `procurement_purchasing` |
   | A suite's product | `Iron.Suites.<Suite>.<Product>` | the same | `<suite>_<product>`, for example `procurement_orders` |
   | Shell modules | | `shell/src/modules/suites/<suite>/<name>/` | |

   Every suite project declares `<IronLayer>Suite</IronLayer>` and `<IronSuite>Procurement</IronSuite>`. The build checks that the layer, the suite, the name and the folder agree, as it does for the other layers. `kernel` and `business` are not allowed as suite names.
3. **Dependencies.** A suite's modules may use the Business and Kernel layers. Inside a suite, the suite's shared masters come first, then its products in process order (ADR-0042). **A suite never references another suite**: the architecture tests refuse it, and so does ESLint in the shell. The Foundation never references a suite.
4. **The placement test** gains one step. No business meaning: Kernel. A master that enterprise products of any kind need, or the products of two or more suites: Business. Needed by two or more products of one suite: that suite's shared master. Owned by one product: that product.
5. **Deployment by purchase** (TF-244).
   - A tenant has one or more suites, recorded in the control plane.
   - The Foundation is always installed. A suite's modules are migrated, seeded and loaded only for tenants that bought it.
   - Adding a suite to an active tenant is an update job with the suite added. Removing a suite switches its modules off and keeps its data, as de-provisioning does.
   - Inside a suite, modules and features are entitlements (ADR-0034), with plans per suite, and changes reach the unit within a minute.
   - Kernel features such as `authorization.role_transfer` belong to a Foundation plan that every tenant has. TF-244 says how that plan sits beside the suite plans.
   - One image per release carries the Foundation and every suite. A unit loads the modules of its tenant's suites at start, from its configuration. Separate images per suite are a later option, if a suite grows large.
6. **Repository.** One repository holds the Foundation and its suites. Because suites never reference each other or point upward, a later suite can move to its own repository and use the Foundation as packages. ADR-0043 says when that would be worth it (a separate team, a separate release rhythm, or a separate licence).

## Steps

Commit in steps that each build and test.

1. **The prompt commit**, then ADR-0043 with its index row. Add a line to ADR-0029: "The third layer became Suite in ADR-0043."
2. **The layer in the build.**
   - `Products` becomes `Suite` in `Directory.Build.props` (the values allowed) and in the architecture tests.
   - The new `IronSuite` property is checked: present on suite projects and absent elsewhere, agreeing with the name and the folder.
   - A fixture proves that one suite referencing another is refused, and so is a schema without its suite's prefix.
   - `src/Products/` becomes `src/Suites/`, with `src/Suites/README.md` (the rule, the placement test, the names) and `src/Suites/Procurement/README.md` (the eProcurement Suite's modules to come, from its PRDs). There are no projects yet.
   - `make ef-add` keeps finding a project by the last segment of its name.
   - `make catalogue` groups licensable modules, features, flags and settings by suite, with the Foundation as its own group, and its drift test follows.
3. **The shell.**
   - `shell/src/modules/products/` becomes `shell/src/modules/suites/`.
   - ESLint refuses an import from one suite into another, and an import of `suites/` from `kernel/` or `business/`. The lint test proves both.
   - The home page groups its module openers by suite, with the Foundation's modules first, so a customer with two suites sees them apart.
4. **The requirement documents.**
   - `docs/prd/foundation/` holds `technical-foundation-PRD.md` and `business-foundation-PRD.md`.
   - `docs/prd/suites/procurement/` holds the product PRDs, the supplier portal surface and the five later scopes.
   - `product-method.md`, `requirements-map.md` and `archive/` stay at `docs/prd/`.
   - Identifiers and prefixes do not change. Every path in the map, the CSVs, the ADRs, the guides and `make prd-check` follows the move.
   - The check gains one rule: a suite's document never depends on another suite's.
5. **TF-244, Suites per tenant** (new, in the technical PRD's tenancy area), with acceptance criteria in the usual format:
   - a tenant provisioned with the eProcurement Suite migrates, seeds and loads its modules;
   - a tenant without it has none of its schemas, endpoints or shell modules;
   - adding the suite to an active tenant is an update job, and removing it keeps the data;
   - inside the suite, a module or feature switches within a minute (TF-040 to TF-046);
   - a plan of one suite cannot name a module of another (error case).
   Section 7 schedules it with TF-241, before the first suite slice. The Business PRD and every suite document name IronSaaS Foundation and the eProcurement Suite in their control sections.
6. **A proposal, not a move.** Read the Business PRD with the new test ("would a suite other than eProcurement need this?"). List, in the report and in STATUS's open questions, every requirement that only the eProcurement Suite would need (purchasing groups is the first candidate), with where it would go: a shared master of the procurement suite. Do not move them; the design session decides.
7. **The README**, restructured for the names:
   - Open with what IronSaaS Foundation is and what a product gets from it. Then the eProcurement Suite as the first suite built on it. Then that other suites, such as a School ERP, can follow. Both are built for regulated industries.
   - A "Where it stands" table: Foundation kernel, Foundation business masters, the eProcurement Suite's products, its supplier portal, and its later products. Each has a status (built, specified, scoped) and a link to its PRD and slice. Nothing is described as working unless it is built.
   - A short section on how a customer buys: suites per purchase, modules and features inside a suite.
   - A short compliance section: the five frameworks of the technical PRD's section 5a, what is built today, and what S3 adds.
   - No counts that change with every task (tests, ADRs).
   - "What works today" in short bullets grouped by area.
   - A licence line (proprietary, all rights reserved; third-party licences in `docs/licences.md`), a new `SECURITY.md` on how to report a vulnerability, and a short "How we work" paragraph (ADR-0023, the definition of done, CLAUDE.md).
   - The first hour, prerequisites, commands and layout, kept, under "For developers"; the layout shows `src/Suites/`.
8. **Names everywhere else.** CLAUDE.md's first line and its layer rule, `.claude/rules/*.md`, the developer guide's "Adding a module" (the placement test with its new step), `docs/prd/README.md`, and STATUS. Keep image names, Compose names, permission names and every other contract of ADR-0029's decision 6.
9. **The finish.** `make prd-check`, `make test`, `make shell-check`, `make shell-a11y` and `make format`; the report.

## Review checklist

- [ ] The third layer is Suite; the build refuses a reference between suites, and one from the Foundation to a suite.
- [ ] Suite, name, folder and schema prefix agree, proven by fixtures.
- [ ] The documents sit under `foundation/` and `suites/procurement/`, and `make prd-check` passes.
- [ ] TF-244 says how a suite is installed per purchase and how plans per suite sit beside the Foundation plan.
- [ ] The README opens with IronSaaS Foundation and the eProcurement Suite, and nothing in it claims what is not built.
- [ ] The proposal lists the procurement-only business requirements without moving them.
