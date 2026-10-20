# Suite layer

A suite is a set of products sold together, with the masters only its products share (ADR-0043). The kernel and business layers below are IronSaaS Foundation, which every tenant has; a tenant buys one or more suites on top of it. The first suite is the eProcurement Suite, in `Procurement/`. Later suites take their own folder and short name, for example a School ERP as `School/` and `school`.

No projects yet. Each suite's folder has a README that names its products and its shared masters; its requirements are in `docs/prd/suites/<suite>/`.

## The placement test

Before creating a module, ask where it belongs:

- It has no business meaning: **Kernel** (`src/Kernel/`).
- A master that enterprise products of any kind need, or the products of two or more suites: **Business** (`src/Business/`).
- Two or more products of one suite need it: that suite's **shared master** (`src/Suites/<Suite>/`).
- One product owns it: that **product** (`src/Suites/<Suite>/`).

A suite's master that a second suite turns out to need moves down to Business; a product never owns what another product needs.

## The rule

- A suite's modules may use Business and Kernel, always through their Contracts projects.
- Inside a suite, the shared masters come first, then the products in process order (ADR-0042). A product may use the suite's masters and an earlier product's Contracts; a master never uses a product, and an earlier product hears from a later one only through domain events.
- A suite never references another suite, not even its Contracts. The Foundation never references a suite: where it must hear from one, it declares an extension point in its Contracts that the suite implements. The architecture tests and ESLint refuse both.
- Every entity is a `BusinessRecord` (owning organisation and sharing mode) or marked `ITenantWide` (TF-220); the architecture tests check it.
- Every module of a suite is licensed per module, in its own code (ADR-0034): its `IModule` declares its licence code and subscription type, and its features. A suite's plan includes it (TF-244), and until a tenant's plan does, its endpoints answer `entitlements.module_disabled` and the shell hides it.
- A suite is installed only for the tenants that bought it: its modules are migrated, seeded and loaded for them alone (TF-244).
- Permission names, audit events, error codes, routes and configuration sections name the module, never the layer or the suite: `<module>.<resource>.<action>`. A module's name is unique across the Foundation and every suite.

## Names

| What | Name |
|---|---|
| Project and root namespace | `Iron.Suites.<Suite>.<Module>`, for a shared master or a product alike |
| Folder | `src/Suites/<Suite>/Iron.Suites.<Suite>.<Module>/` |
| Contracts | `Iron.Suites.<Suite>.<Module>.Contracts` in the module's `Contracts/` folder |
| Layer and suite in both project files | `<IronLayer>Suite</IronLayer>` and `<IronSuite><Suite></IronSuite>` |
| Schema | `<suite>_<module>`, the suite's short name in lowercase: `procurement_orders` |
| Tests | `tests/Suites/<Suite>/Iron.Suites.<Suite>.<Module>.Tests/` |
| Shell module | `shell/src/modules/suites/<suite>/<name>/`, with `suite: "<suite>"` in its manifest |
| Requirements | `docs/prd/suites/<suite>/` |

`<Suite>` is one word in PascalCase; `Kernel`, `Business` and `Foundation` are not suite names. The build stops a Suite project without `IronSuite` (`IRON0003`), a suite name it cannot use (`IRON0004`), and the architecture tests a name, folder or schema that names another suite.

## Adding a suite

1. Agree its short name and its products in the design session, and record them in an ADR beside ADR-0043.
2. Create `src/Suites/<Suite>/README.md` with its products in process order and its shared masters, and `docs/prd/suites/<suite>/` for its requirements.
3. Add its modules as below; add its shell modules under `shell/src/modules/suites/<suite>/`.

## Adding a module to a suite

1. Apply the placement test to every master the module needs: those two or more suites need belong in Business first, those two or more of this suite's products need belong in a shared master.
2. Copy the layout of a kernel module (`src/Kernel/Iron.Kernel.Identity` is the template): `Api/`, `Services/`, `Data/`, `Contracts/` as its own project, and a `README.md`. Declare `<IronLayer>Suite</IronLayer>` and `<IronSuite><Suite></IronSuite>` in both project files.
3. Name the context's schema `[Schema("<suite>_<module>")]` and generate its first migration with `make ef-add MODULE=<Module> NAME=Initial`.
4. Add both projects to `Foundation.slnx` and to `tests/Iron.Architecture.Tests`, and the module to the hosts that serve it.
5. Run `make check`: it names any reference that points up, crosses to another suite or passes a Contracts project.
6. Declare its licence and features, add it to `Iron.Host.Api.UnitModules`, run `make catalogue` and commit the catalogue (the module appears in its suite's group); add `licence` and `suite` to its shell manifest.
