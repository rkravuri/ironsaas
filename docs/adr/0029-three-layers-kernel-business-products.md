# ADR-0029: Three layers: kernel, business, products

**Status:** Accepted
**Date:** 2026-10-19
**Decided by:** Product owner and architect

The third layer became Suite in ADR-0043.

## Context

S1 and the first S2 tasks built technical mechanisms. The Functional Foundation PRD mixes two different things: the shared business masters that every product needs, and the products themselves with their own documents and life cycles. Without a rule, business meaning drifts into technical modules (an organisation unit that knows what a legal entity is) and products start to own masters that other products need. A1 is about to build organisation units, which the owner places partly in the functional foundation, so the layers are made explicit in names, folders, schemas and build checks before any further module is added.

## Decision

1. Foundation code lives in three layers.
   - **Kernel** holds technical mechanisms that know nothing about any business domain: the vocabulary (results, errors, ids, tenant context, money, paging, principals, permissions), the shared libraries (data, API, telemetry, security), and the modules identity, authorization (with the organisation tree, scopes, roles, record policies and delegation of duties), audit, files, tenancy, fleet, and later entitlements, flags, settings, workflow and approvals engine, numbering service, notification delivery, forms engine, business calendar, tasks.
   - **Business** holds the shared business masters and the business configuration of kernel mechanisms: organisations and legal entities, purchasing groups, departments, people (designations, job grades, managers, business groups), items, commodities, categories, units of measure, chart of accounts, budgets, tax codes, HSN and GL codes, currencies and conversion rates, financial years and periods, payment and shipping terms, locations, projects as cost objects, the supplier master, content grouping, lookups, approval configurations and the delegation of authority matrices, number series, notification templates.
   - **Products** hold documents and processes with their own life cycles: supplier onboarding, purchase requests, purchase orders, advanced shipment notices, goods and service receipts, invoicing, sourcing (RFx), auctions, contracts, projects and tasks as a product, forms and surveys, expenses. The supplier portal is a surface, not a product: each product contributes its supplier views.
2. **The placement test.** It has no business meaning: Kernel. Two or more products would need to own it: Business. One product owns it: Products.
3. **Names.**

   | Layer | Project and root namespace | Folder | Schema |
   |---|---|---|---|
   | Kernel vocabulary | `Iron.Kernel`, one flat namespace | `src/Kernel/Iron.Kernel/` | none |
   | Kernel libraries | `Iron.Kernel.Data`, `.Api`, `.Telemetry`, `.Security` | `src/Kernel/Iron.Kernel.<Name>/` | none |
   | Kernel modules | `Iron.Kernel.<Module>` | `src/Kernel/Iron.Kernel.<Module>/` | `kernel_<module>` |
   | Business modules | `Iron.Business.<Module>` | `src/Business/Iron.Business.<Module>/` | `business_<module>` |
   | Products | `Iron.Products.<Product>` | `src/Products/Iron.Products.<Product>/` | `products_<product>` |
   | Hosts | `Iron.Host.<Surface>` | `src/Hosts/Iron.Host.<Surface>/` | none |
   | Tests | `<Project>.Tests` | `tests/<Layer>/<Project>.Tests/`; architecture and host tests stay at `tests/` | none |
   | Shell modules | | `shell/src/modules/{kernel,business,products}/<name>/` | none |

   The kernel vocabulary is one namespace so that no second segment under `Iron.Kernel` can ever be both a vocabulary area and a module (the S1 namespace `Iron.Core.Tenancy` would otherwise collide with the module `Iron.Kernel.Tenancy`). The schema keeps the module's existing short name: `kernel_identity`, `kernel_authz`, `kernel_audit`, `kernel_files`, `kernel_tenancy`, `kernel_fleet`.
4. **Dependencies go downward only**, and between modules only through a module's Contracts project: Products may use Business and Kernel; Business may use Kernel; Kernel uses only Kernel. Hosts compose any layer; tests and tools may reference what they test or drive. Every project declares `<IronLayer>` in its project file, one of `Kernel`, `Business`, `Products`, `Host`, `Test`, `Tool`; a missing or unknown value fails the build, and the architecture tests refuse an upward reference and a reference into another module's `Api`, `Services` or `Data`.
5. **Extension points point down.** When the kernel needs something only a business module can supply, the kernel declares an interface in its Contracts and the business module implements it; the kernel never references Business. The first one is the organisation kinds (A1).
6. **What keeps its name.** Permission names, audit event names, error codes, API routes, configuration sections, image names and secret paths are contracts with stored data, roles and clients; they name the module, not the layer, and do not change.
7. **Schemas start from a new baseline, once.** Each kernel module's migrations are replaced by a new `Initial` migration for its prefixed schema. This is allowed only because no database outside laptops and CI holds data today; from the first deployment outside a laptop, migrations are append-only again, as ADR-0008 says. The schema check refuses a module schema without its layer prefix.

### How the build holds the rule

- `Directory.Build.props` has the target `IronCheckLayer`, which runs before every compile and stops the build with `IRON0001` when a project declares no `IronLayer` and `IRON0002` when the value is not one of the six. The architecture tests read the same property from every project file and say the same thing, so `make check` names every offender at once.
- The layer must agree with the name and the folder: `Iron.Kernel*` under `src/Kernel/`, `Iron.Business.*` under `src/Business/`, `Iron.Products.*` under `src/Products/`, `Iron.Host.*` under `src/Hosts/`, tests under `tests/`, tools under `tools/`. A project cannot change its layer by declaring another one.
- Inside the three layers: the vocabulary references nothing; a kernel library references the vocabulary and other kernel libraries; a Contracts project references the vocabulary only; a module references the kernel libraries, the vocabulary and Contracts projects (its own and other modules'), never another module's implementation project, and never a project of a higher layer. A host references any project of the three layers, never another host, a test or a tool.
- The private-namespace rule of ADR-0001 covers modules of every layer: no assembly uses another module's `Api`, `Services` or `Data` types.
- The model rule of TF-220 (every entity is a `BusinessRecord` or marked `ITenantWide`) applies to Business and Products contexts; kernel contexts keep the S1 rule. The architecture tests also check that a context's schema carries its project's layer prefix, and `SchemaCheck` refuses a schema without one of the three prefixes in a live database.
- In the shell, ESLint's `no-restricted-imports` refuses an import of `business/` or `products/` inside `modules/kernel/`, and of `products/` inside `modules/business/`.
- `make ef-add MODULE=<Module> NAME=<Name>` finds the project by the last segment of its name (`MODULE=Identity` is `src/Kernel/Iron.Kernel.Identity`), and refuses a segment that matches none or more than one.
- Two more names keep theirs, for the same reason as decision 6: the module names the modules report (`identity`, `authz`, ...) and the telemetry names (the `Iron.Authorization` meter and the `Iron.Worker` activity source; the pipeline listens to `Iron.*`).

### The mapping from S1 names

ADRs before this one keep the names of their time. This table translates them.

| Before | Today | Folder today |
|---|---|---|
| `Iron.Core` (namespaces `Iron.Core.Correlation`, `.Finance`, `.Identifiers`, `.Paging`, `.Permissions`, `.Principals`, `.Results`, `.Tenancy`, `.Text`) | `Iron.Kernel` (one namespace, `Iron.Kernel`) | `src/Kernel/Iron.Kernel/` |
| `Iron.Data` | `Iron.Kernel.Data` | `src/Kernel/Iron.Kernel.Data/` |
| `Iron.Api` | `Iron.Kernel.Api` | `src/Kernel/Iron.Kernel.Api/` |
| `Iron.Telemetry` | `Iron.Kernel.Telemetry` | `src/Kernel/Iron.Kernel.Telemetry/` |
| `Iron.Security` | `Iron.Kernel.Security` | `src/Kernel/Iron.Kernel.Security/` |
| `Iron.Identity`, `Iron.Identity.Contracts` | `Iron.Kernel.Identity`, `Iron.Kernel.Identity.Contracts` | `src/Kernel/Iron.Kernel.Identity/` |
| `Iron.Authorization`, `Iron.Authorization.Contracts` | `Iron.Kernel.Authorization`, `Iron.Kernel.Authorization.Contracts` | `src/Kernel/Iron.Kernel.Authorization/` |
| `Iron.Audit`, `Iron.Audit.Contracts` | `Iron.Kernel.Audit`, `Iron.Kernel.Audit.Contracts` | `src/Kernel/Iron.Kernel.Audit/` |
| `Iron.Files`, `Iron.Files.Contracts` | `Iron.Kernel.Files`, `Iron.Kernel.Files.Contracts` | `src/Kernel/Iron.Kernel.Files/` |
| `Iron.Tenancy`, `Iron.Tenancy.Contracts` | `Iron.Kernel.Tenancy`, `Iron.Kernel.Tenancy.Contracts` | `src/Kernel/Iron.Kernel.Tenancy/` |
| `Iron.Fleet`, `Iron.Fleet.Contracts` | `Iron.Kernel.Fleet`, `Iron.Kernel.Fleet.Contracts` | `src/Kernel/Iron.Kernel.Fleet/` |
| `Iron.Host.Api`, `.Worker`, `.ControlPlane`, `.Fleet` in `src/` | the same names | `src/Hosts/Iron.Host.<Surface>/` |
| `Iron.Core.Tests` | `Iron.Kernel.Tests` | `tests/Kernel/Iron.Kernel.Tests/` |
| `Iron.<Name>.Tests` for Data, Api, Telemetry, Security, Identity, Authorization, Audit, Files, Tenancy, Fleet | `Iron.Kernel.<Name>.Tests` | `tests/Kernel/Iron.Kernel.<Name>.Tests/` |
| `Iron.Architecture.Tests`, `Iron.Host.Tests` | the same names | `tests/` (unchanged) |
| `Iron.Smoke` | the same name | `tools/Iron.Smoke/` (unchanged) |
| `products/` (a README) | the Products layer | `src/Products/` |
| `src/Iron.Shell/` (a placeholder README from before T15) | removed; the shell is `shell/` | |
| Namespaces `Iron.<Name>` and below, for the ten libraries and modules above | `Iron.Kernel.<Name>` and below | |
| Schemas `identity`, `authz`, `audit`, `files`, `tenancy`, `fleet` | `kernel_identity`, `kernel_authz`, `kernel_audit`, `kernel_files`, `kernel_tenancy`, `kernel_fleet` | each with one migration, `Initial` |
| The data tests' sample schema `sample` | `kernel_sample` | |
| `shell/src/modules/identity/` | the same module | `shell/src/modules/kernel/identity/` |
| `make ef-add MODULE=Identity` (found `src/Iron.Identity`) | `make ef-add MODULE=Identity` (finds `src/Kernel/Iron.Kernel.Identity`) | |

Unchanged on purpose (decision 6): permission names such as `identity.users.read` and `authz.roles.read`, audit event names, error codes, routes under `/api/v1/` and `/connect/`, configuration sections, the image names `foundation/iron-api`, `iron-worker`, `iron-control-plane`, `iron-fleet` and `iron-shell`, the assemblies `Iron.Host.*.dll` the images start, and the secret paths `tenants/<slug>/*` and `control`.

## Options considered

- Keep the S1 names and use the layer names only for new code. Rejected: the kernel would have no name of its own, and "the kernel uses only the kernel" could not be read from a reference.
- Keep sub-namespaces in the vocabulary (`Iron.Kernel.Results`, `Iron.Kernel.Tenancy`). Rejected: a vocabulary area and a module would share a namespace (`Iron.Kernel.Tenancy`), and every later module name would have to be checked against the vocabulary.
- One `Directory.Build.props` per layer folder that sets `IronLayer`. Rejected: moving a project to another folder would change its layer without a line in its own file; the project file says what it is.
- A solution or a repository per layer. Rejected: P1 (simplicity) and one build; the downward rule is a test, not a wall.
- Keep the S1 schema names and prefix only new schemas, or add a bridge that renames them in place. Rejected: two conventions forever, and bridge code for data that exists only on laptops and in CI.
- Products under a top-level `products/` folder, as ADR-0001 said. Rejected: one `src/` tree with a folder per layer reads the same way at every depth.

## Consequences

- Easier: the layer of any project, namespace, schema or shell module is in its name; the placement test answers where a new module goes before its first line; the build refuses a reference that points up.
- Harder: every path in the guides changed once; ADRs before 0029 use the names of their time, and this ADR's table translates them. A kernel feature that needs business meaning must be an interface in kernel Contracts with a business implementation, which takes more lines than a field.
- Laptops are reset once (`make reset`) and their tenants provisioned again, because the schemas moved. CI starts from empty databases on every run.
- The Functional Foundation PRD will be split into a Business PRD and one PRD per product, in the design session, before any business or product slice.
- Revisit: when the first business module arrives, check that `IronLayer`, the downward rule and the schema prefix need no change for it; when the first deployment outside a laptop holds data, migrations are append-only again and a schema rename becomes a migration.
