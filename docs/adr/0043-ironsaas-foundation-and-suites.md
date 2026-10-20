# ADR-0043: IronSaaS Foundation and suites

**Status:** Accepted
**Date:** 2026-10-20
**Decided by:** Product owner and architect

## Context

ADR-0029 put the code in three layers: Kernel, Business and Products. The PRD split (ADR-0041, ADR-0042) then wrote seven procurement products, the supplier portal surface and five later procurement scopes. IronSaaS will sell more than procurement; a School ERP is the next candidate. Its products would share masters with each other (pupils, classes, terms) that procurement never needs, as procurement's products share masters (purchasing groups) that a school never needs. With one Products layer such a master has two bad homes: Business, where every tenant of every line carries it, or one product, which the others then depend on. And a customer buys procurement or a school system, rarely both: a unit that migrates, seeds and loads every product carries schemas, endpoints and seeds its tenant never bought. The design session decided the structure on 2026-10-20 (`docs/prompts/L2-suites.md`).

## Decision

1. **Names.**
   - **IronSaaS Foundation** is the kernel and business layers together: what every tenant has.
   - A **suite** is a set of products sold together, with the masters only its products share.
   - The first suite is the **eProcurement Suite**, short name `procurement`. Later suites take their own short name, for example `school`.
   - The code namespace stays `Iron.*`.
2. **Layers.** Kernel and Business stay. The third layer is `Suite`, replacing `Products`.

   | What | Project and root namespace | Folder | Schema |
   |---|---|---|---|
   | A suite's shared master | `Iron.Suites.<Suite>.<Module>` | `src/Suites/<Suite>/Iron.Suites.<Suite>.<Module>/` | `<suite>_<module>`, for example `procurement_purchasing` |
   | A suite's product | `Iron.Suites.<Suite>.<Product>` | `src/Suites/<Suite>/Iron.Suites.<Suite>.<Product>/` | `<suite>_<product>`, for example `procurement_orders` |
   | Its tests | `Iron.Suites.<Suite>.<Name>.Tests` | `tests/Suites/<Suite>/Iron.Suites.<Suite>.<Name>.Tests/` | none |
   | Its shell modules | | `shell/src/modules/suites/<suite>/<name>/` | none |
   | Its requirements | | `docs/prd/suites/<suite>/` | none |

   Every suite project declares `<IronLayer>Suite</IronLayer>` and `<IronSuite>Procurement</IronSuite>`. `<Suite>` is one word in PascalCase; `<suite>` is the same word in lowercase, the short name. `Kernel`, `Business` and `Foundation` are not suite names: the first two are layers, and `foundation` is the catalogue's name for everything outside a suite.
3. **Dependencies.**
   - A suite's modules may use Business and Kernel.
   - Inside a suite, the shared masters come first, then the products in process order (ADR-0042): a product may use the suite's masters, a master never uses a product.
   - A suite never references another suite. The architecture tests and ESLint refuse it.
   - The Foundation never references a suite. Where the Foundation must hear from a suite, it declares an extension point in its Contracts, as ADR-0029 decision 5 and ADR-0042 decision 2 say.
4. **The placement test**, with its new step:
   - No business meaning: Kernel.
   - A master that enterprise products of any kind need, or the products of two or more suites: Business.
   - Needed by two or more products of one suite: that suite's shared master.
   - Owned by one product: that product.
5. **Deployment by purchase (TF-244).**
   - A tenant has one or more suites, recorded in the control plane.
   - The Foundation is always installed. A suite's modules are migrated, seeded and loaded only for tenants that bought it.
   - Adding a suite to an active tenant is an update job. Removing one switches its modules off and keeps the data: its schemas, rows and files stay, and adding it again finds them.
   - Inside a suite, modules and features are entitlements (ADR-0034), with plans per suite, reaching the unit within a minute.
   - The business modules and the kernel's features, such as `authorization.role_transfer`, belong to a Foundation plan every tenant has. A tenant holds one Foundation plan and one plan per suite it bought. Each plan names only the modules and features of its own group, and the tenant's entitlements are the union of its plans. TF-244 holds the criteria.
   - One image per release carries the Foundation and every suite. A unit loads its tenant's suites at start, from its configuration. Separate images per suite are a later option.
6. **Repository.** One repository holds the Foundation and its suites. A suite could move to its own repository later and use the Foundation as packages. That is worth it when one of three things holds: a separate team owns the suite, the suite needs a separate release rhythm, or the suite is sold under a separate licence. Until then one repository keeps one build, one test run, and one change for both sides.
7. **What keeps its name.** Every contract of ADR-0029 decision 6 stays: permission names, audit event names, error codes, routes, configuration sections, image names, Compose names and secret paths name the module, never the layer or the suite. Because they name the module, a module's name is unique across the Foundation and every suite; the catalogue already refuses a licence code declared twice.

### How the build holds it

- `Directory.Build.props`: the target `IronCheckLayer` accepts `Suite` in place of `Products` (`IRON0002` lists the six layers). It stops the build with `IRON0003` when a Suite project declares no `IronSuite` or another project declares one, and with `IRON0004` when the value is not a suite name. A suite project carries its suite into its assembly as `AssemblyMetadata("IronSuite", ...)`, so `IModule.Suite` reads it there and no module repeats it in code.
- The architecture tests read the same properties. A Suite project is named `Iron.Suites.<Suite>.*`, lives under `src/Suites/<Suite>/`, and declares that `<Suite>` as its `IronSuite`; no other project declares one. A reference from one suite's project to another suite's is refused, as is any reference from Kernel or Business to a suite. A suite context's schema starts with `<suite>_`. A suite module declares a licence, as a Business module does. Fixtures prove each refusal.
- `SchemaCheck` accepts `kernel_`, `business_` and the prefixes of the suites its caller names; the kernel names no suite.
- `make ef-add MODULE=<Module>` finds `src/Suites/<Suite>/Iron.Suites.<Suite>.<Module>` by the last segment of its name, as it finds the kernel and business modules.
- `make catalogue` writes the catalogue grouped by suite (format version 2): the group `foundation` first, then each suite, each with its licensed modules, features, flags and settings. The host test still fails while the file and the code differ.
- In the shell, ESLint refuses an import of `modules/business` or `modules/suites` inside `modules/kernel`, of `modules/suites` inside `modules/business`, and of another suite's folder inside `modules/suites/<suite>` (the local rule `iron/suite-imports`). A manifest under `suites/<suite>/` declares `suite: "<suite>"`, and a test holds the declaration to the folder. The home page groups the module openers by suite, the Foundation's first.
- `make prd-check` refuses a dependency from one suite's document on another suite's.

### The mapping from ADR-0029's names

ADRs before this one keep the names of their time. This table translates them.

| Before | Today |
|---|---|
| The Products layer, `<IronLayer>Products</IronLayer>` | The Suite layer, `<IronLayer>Suite</IronLayer>` with `<IronSuite>` |
| `Iron.Products.<Product>` in `src/Products/` | `Iron.Suites.<Suite>.<Product>` in `src/Suites/<Suite>/` |
| Schema `products_<product>` | Schema `<suite>_<product>`, for example `procurement_orders` |
| `shell/src/modules/products/<name>/` | `shell/src/modules/suites/<suite>/<name>/` |
| `docs/prd/technical-foundation-PRD.md`, `business-foundation-PRD.md` | `docs/prd/foundation/` |
| `docs/prd/products/*`, `docs/prd/surfaces/supplier-portal-PRD.md` | `docs/prd/suites/procurement/` |
| "the products" as the layer above Business | the suites, and inside one suite, its products |

## Options considered

- Keep one Products layer for the products of every line. Rejected: a master that one line's products share would sit in Business and reach every tenant, or sit inside one product that the others then depend on.
- A fourth layer, suite masters between Business and Products. Rejected: two layers for one boundary. A suite's masters and products differ only in their order inside the suite, which ADR-0042 already states.
- Namespaces without the layer, `Iron.Procurement.*`. Rejected: ADR-0029 keeps the layer in every name, so a reference can be read without opening the project.
- One repository per suite from the start. Rejected for now: there is one team, one release rhythm and one licence today. Packages would add a release step to every Foundation change and split one change across two pull requests. Revisit under decision 6's conditions.
- One image per suite now. Rejected for now: one image keeps one build, one scan and one release, and the unit loads only its tenant's suites. Revisit when an image's size or start time matters.
- Install every suite for every tenant and let entitlements alone decide. Rejected: every tenant would carry schemas, seeds and endpoints it never bought, and the answer to "what does this system hold" would grow with every suite.

## Consequences

- Easier: a new line of business has its own place for its masters; a tenant carries only what it bought; the layer and the suite of any project, schema, shell module or document are in its name.
- Harder: the unit composes its modules from its tenant's configuration (TF-244), which provisioning, the update job and the catalogue must honour; plans gain a group; a suite's tests need a unit with that suite.
- The paths of the requirement documents moved once; their identifiers did not.
- Revisit: before the second suite's first slice, review the Business requirements that only procurement needs (the L2 report proposes them) and move those that belong to a procurement master; when one of decision 6's conditions holds, give the suite its own repository.
