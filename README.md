# IronSaaS Foundation

IronSaaS Foundation is a multi-tenant SaaS platform for regulated industries: one technical and business base on which IronSaaS builds suites of products. Every tenant has the Foundation: tenancy, identity, permissions and organisations, audit, entitlements, flags and settings, telemetry, provisioning, a UI shell in one design language, and the business masters that enterprise products share.

The first suite is the **eProcurement Suite**: source to pay, from supplier management through requests, orders and receiving to invoicing, with one portal for suppliers. Other suites can follow on the same Foundation, a School ERP among them, each with its own products and the masters only its products share (ADR-0043). Everything is built for regulated industries: audit, access control and electronic records are platform requirements, not features a product adds.

## Where it stands

| Part | What it is | Status | Requirements | Slices |
|---|---|---|---|---|
| Kernel | The technical mechanisms: tenancy and the fleet, identity, authorization and organisations, audit, entitlements, flags, settings, files, telemetry, the shell | Built in part: S1 and S2 are done, S3 is next | [Technical Foundation PRD](docs/prd/foundation/technical-foundation-PRD.md) | [S1](docs/slices/S1-walking-skeleton.md), [S2](docs/slices/S2-access-control-organisations-and-design.md), [S3](docs/slices/S3-trust-and-compliance.md); S4 to S9 in the PRD's section 7.1 |
| Business masters | Organisations and legal entities, people, items, finance and budgets, locations, the supplier master, lookups, approvals and the delegation of authority, numbering and templates | Specified | [Business Foundation PRD](docs/prd/foundation/business-foundation-PRD.md) | B1 to B4 in the technical PRD's section 7.2 |
| eProcurement Suite: products | Supplier management, Contracts, Catalogue, Requests, Orders, Receiving, Invoicing | Specified | one PRD each in [`docs/prd/suites/procurement/`](docs/prd/suites/procurement/) | PR1 to PR7 in the technical PRD's section 7.2 |
| eProcurement Suite: supplier portal | One portal for all the suppliers of all customers, on the kernel's Supplier Network | Specified | [Supplier portal PRD](docs/prd/suites/procurement/supplier-portal-PRD.md) | PR8, after the kernel's S8 |
| eProcurement Suite: later products | Sourcing, auctions, expenses, projects and tasks, surveys | Scoped: one page each | the `*-scope.md` pages in [`docs/prd/suites/procurement/`](docs/prd/suites/procurement/) | not planned yet |
| Other suites | A School ERP and others | Not started | | |

Built means it runs and its tests pass; specified means its requirements are written and accepted; scoped means one page says what it will be. Suites per tenant (TF-244) are specified and come with content visibility (TF-241), before the first business slice.

## How a customer buys

A customer is a tenant with a unit of its own: its own database, storage container, secrets and containers. Every tenant has IronSaaS Foundation, with a Foundation plan that says which business modules and kernel features it has; today those plans are Standard and Enterprise. On top, a tenant buys one or more suites, each with a plan of its own modules and features, and a suite is installed only for the tenants that bought it (TF-244). Plans, entitlements, seats and a change of plan that reaches the tenant within a minute are built (ADR-0034); suites per tenant are specified.

## Compliance

Five frameworks are in scope for every tenant (the technical PRD's section 5a): SOC 2 Type II, ISO 27001, GDPR, India's DPDP Act, and 21 CFR Part 11 with EU Annex 11. No certification is claimed; the order of certification is an open operational decision.

Built today:

- Every endpoint declares its permission, and access is denied by default. Roles are granted at the tenant, a unit or a record; record policies, sharing across units, delegation with both names on record, access reviews, conflict rules for duties one person must not hold together, and an explanation of every decision.
- An append-only audit trail of sign-ins, permission denials, provisioning steps and every state change, written in the same transaction as the change; reads across units are audited.
- Each tenant isolated in its own database, database roles, storage container and secrets path; secrets in the store, never in configuration files.
- A correlation id on every request, traced end to end and shown on every error.
- A secret scan (gitleaks) and a dependency scan (Trivy) on every change.

S3 adds, as the technical PRD's section 7.1 plans it: multi-factor authentication, single sign-on, the sessions console, password policy and lockout, the audit log's hash chain, field-level change history, electronic signatures and their manifestation, audit search and retention, security headers, rate limits and the vulnerability pipeline.

## What works today

**Tenants and operations**

- The control plane registers a tenant and the fleet provisions it in about fifteen seconds: its database roles and database, storage container, secrets path and token, migrations, seeds, and a unit of three containers (API, worker, shell) behind the ingress, checked through the ingress. A failed step resumes; an update job gives an active unit new images; de-provisioning keeps the data.
- The control plane decides what each tenant has, by plan, module, feature and seat, and which flags and kill switches apply; a change reaches the tenant's API and shell within a minute.

**People and access**

- A person follows an invitation to the tenant's own hostname, sets a password and signs in with a server-side session; a service account gets a 15-minute token with the client credentials grant.
- A tenant administrator shapes who may do what: the organisation tree, roles and their scopes, record policies, shared records, delegation, access reviews, the unit a person works for, units created, moved, merged and deactivated, conflict rules, and why a person can or cannot do something.
- The tenant's settings resolve from a person through the units to the tenant.

**The shell**

- One React build for every tenant, in the design language of ADR-0024: a home page that opens each module, grouped by suite with the Foundation's first; search with Ctrl+K; help; the session warning; the console's pages; light and dark schemes and two densities; a mark of its own per tenant; keyboard operation; WCAG 2.1 AA on every page of the gallery.

**Checks**

- The architecture tests hold the layers, the suites and the module boundaries; the requirement documents are checked on every change; the tests run on real PostgreSQL, Azurite and OpenBao; a smoke test and browser tests run against the live stack; the pipeline builds the images and proves them.

## Licence

Proprietary. Copyright IronSaaS, all rights reserved. The licences of third-party fonts, images and data are in [`docs/licences.md`](docs/licences.md); every package's licence is beside its version in `Directory.Packages.props` and `shell/package.json`.

To report a vulnerability, see [SECURITY.md](SECURITY.md).

## How we work

The platform is built in slices, each a thin increment that can be demonstrated end to end (the technical PRD's section 7). A design session writes each task as a prompt in `docs/prompts/`; Claude Code builds it on a branch of its own, commits the prompt before the work, and reports with the checks, the screenshots and the answers to the design brief's questions; the design session then merges it or answers with corrections (ADR-0023). Every decision with real alternatives is an ADR, every requirement has an identifier that its tests name, and the build refuses what the rules forbid.

## For developers

### The first hour: a fresh checkout to a provisioned tenant and a visible trace

Timings are from the reference machine, a laptop with 16 GB of memory and WSL 2 limited to 12 GB with 8 GB of swap (`.wslconfig`, see the runbook's prerequisites), with a warm image cache; the first `make images` and `make up` download about 2 GB more.

| Step | Command | Expect | Time |
|---|---|---|---|
| 1 | Prerequisites below | `dotnet --version` 10.0.x, `docker compose version` 2.x, `node --version` v26 from WSL | 10 min |
| 2 | `git clone <repo> ~/foundation && cd ~/foundation` | | 1 min |
| 3 | `make build && make test` | build clean, every test passes (Docker must run: the tests start their own PostgreSQL, Azurite and OpenBao) | 5 min |
| 4 | `make up` | platform healthy, the secret store initialised (its root token in `infra/compose/.env`), endpoints printed | 3 min |
| 5 | `make images` | the images `foundation/iron-*:dev` | 10 min |
| 6 | `make secret-control && make migrate-control && make seed-control` | control database and operator | 1 min |
| 7 | terminal 2: `make run-control`; terminal 3: `make run-fleet` | both listening | 1 min |
| 8 | `make provision TENANT=acme` | the provisioning steps, then the invitation link; the tenant starts on the `standard` plan | 1 min |
| 9 | `curl.exe -i http://acme.localhost/health/ready` (from Windows) or the browser | 200 with `X-Correlation-Id` | |
| 10 | Open `http://grafana.localhost`, Explore, Tempo, paste that correlation id as the trace id | the request's spans | 1 min |
| 11 | Open the invitation link, set a password, sign in | the home page: under IronSaaS Foundation, an opener for Identity and one for the console, each with its pages; Ctrl+K searches them | 2 min |
| 12 | Open Users from the Identity opener | the grid lists the administrator and the smoke client | 1 min |

`make smoke TENANT=acme` then proves the whole path from the outside in about three minutes, the role scoped to a unit, the plan change and the kill switch included; `make apply-plan TENANT=acme PLAN=Enterprise` gives the tenant role export and import. The runbook `docs/guides/local-stack.md` has every command, the map of the stack and the troubleshooting table.

### Prerequisites

- Linux, or WSL 2 with Ubuntu. The runtime is Linux; nothing targets Windows. Clone inside the Linux file system (for example `~/foundation`), not under `/mnt/c`.
- .NET SDK 10.0 inside WSL:

  ```bash
  sudo apt update && sudo apt install -y libicu-dev make git python3 curl unzip
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh && /tmp/dotnet-install.sh --channel 10.0
  cat >> ~/.bashrc <<'EOT'
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$PATH:$HOME/.dotnet:$HOME/.dotnet/tools"
  EOT
  source ~/.bashrc && dotnet --version
  ```

- Docker with the Compose plugin: Docker Engine inside WSL (`sudo apt install docker.io docker-compose-v2`, add your user to the `docker` group, log in again) or Docker Desktop with WSL integration. `docker compose version` prints 2.x.
- Node.js 26 inside WSL, not the Windows one (`which npm` must not point under `/mnt/c`): `nvm install 26` with [nvm](https://github.com/nvm-sh/nvm).
- Optional: `mkcert` for a trusted TLS certificate (`make certs`); Playwright browsers for `make e2e` (`cd shell && npm run e2e:install`).

### Commands

`make` with no target lists every command with a line of help. The ones used daily:

```bash
make build / make test / make check                     # the .NET side (make test starts its own PostgreSQL, Azurite, OpenBao)
make format                                              # dotnet format and Prettier (ADR-0040)
make shell-install / make shell-check / make shell-dev   # the shell (npm ci; lint, format check, tests, build; Vite on :5173)
make shell-a11y                                          # the accessibility scan of the gallery, no stack needed
make up / make up-slim / make down / make reset          # the platform
make images                                              # the images at tag dev
make run-api / make run-worker                           # the dev tenant from source on :5100 and :5101
make secret-control / migrate-control / seed-control     # the control plane's secret, database and operator
make run-control / make run-fleet                        # the control plane from source on :5200
make up-control / make down-control                      # the control plane as containers instead
make provision TENANT=x / update-unit TENANT=x / deprovision TENANT=x
make apply-plan TENANT=x PLAN=y [DRY_RUN=1]              # preview a plan for a tenant, then apply it
make flag KEY=k VALUE=v [TENANT=x]                       # a flag's rule for a tenant or the platform
make kill-switch PATH=imports ON=true REASON="..."       # a kill switch, for the platform or TENANT=x
make catalogue                                           # what the units can license, switch and set, grouped by suite
make prd-csv / make prd-check                            # the requirement documents' CSVs, and their check
make smoke / make e2e [TENANT=x] / make integration      # the smoke test, the browser tests, the pipeline's stage
make ef-add MODULE=Identity NAME=AddColumn               # a migration for a module context (the last segment of its name)
```

### Repository layout

```
Foundation.slnx              solution (XML format, edit by hand)
Directory.Build.props        compiler settings for every project, and the layer and suite check
Directory.Packages.props     every package version, once, with its licence
global.json                  SDK band and the test runner
Makefile                     the developer commands
src/
  Kernel/                    technical mechanisms, no business meaning (ADR-0029)
    Iron.Kernel/             the vocabulary: results, errors, ids, tenant, money, paging, principals, permissions
    Iron.Kernel.Data/  .Api/  .Telemetry/  .Security/                 kernel libraries
    Iron.Kernel.Identity/  .Authorization/  .Audit/  .Files/          tenant modules
    Iron.Kernel.Entitlements/  .Flags/  .Settings/                    tenant modules (S2)
    Iron.Kernel.Tenancy/  .Fleet/                                     control plane modules
      Api/  Services/  Data/  Contracts/  README.md                   inside every module
  Business/                  shared business masters (empty until the first business slice; README)
  Suites/                    one folder per suite (ADR-0043; README)
    Procurement/             the eProcurement Suite: Iron.Suites.Procurement.* (empty until its first slice; README)
  Hosts/
    Iron.Host.Api/  Iron.Host.Worker/            tenant unit hosts (+ Dockerfiles)
    Iron.Host.ControlPlane/  Iron.Host.Fleet/    control plane hosts (+ Dockerfiles)
shell/                       the React shell: one build for every tenant (+ Dockerfile, e2e/, lint/ for its own rule);
                             modules in src/modules/kernel, business and suites/<suite>
tests/                       Iron.Architecture.Tests (the build checks), Iron.Host.Tests, Kernel/ with a test
                             project per kernel project, and Iron.Testing (the services the tests run on)
tools/                       Iron.Smoke (the smoke test) and prd_check.py (the requirement documents' check)
infra/compose/               the laptop platform: compose.yaml, configs, scripts, rendered units/
.github/workflows/           ci.yml and release.yml
docs/                        prd/ (foundation/, suites/procurement/), adr/, guides/, slices/, prompts/
```

### What the build enforces

- Warnings are errors in every project; `dotnet format` must make no change.
- Every package version is declared once in `Directory.Packages.props`, with its licence; the shell's are in `shell/package.json`.
- Layers and suites: every project declares `<IronLayer>`, and a suite's project its `<IronSuite>` (the build stops without them). References go downward only: a suite may use Business and Kernel, Business may use Kernel, Kernel uses only Kernel; a suite never references another suite (ADR-0029, ADR-0043).
- Module boundaries: a module may reference the kernel vocabulary, the kernel libraries and `Contracts` projects only. The compiler enforces it through project references; `tests/Iron.Architecture.Tests` names any violation, including type references into another module's `Api`, `Services` and `Data` namespaces.
- No Foundation assembly reads the system clock directly; time comes from an injected `TimeProvider`.
- Every module DbContext passes the model rules, its schema carries its layer's prefix or its suite's short name, and its migrations match its model; the schema check runs over the migrated schema.
- Every endpoint declares a permission, `RequireSignedIn` or `AllowAnonymous`; a host does not start otherwise, and the anonymous list is fixed by a test.
- The requirement documents: every identifier once, every old identifier in the map, criteria in the Given, When, Then form with an error case, each CSV equal to its markdown, and dependencies down the layers, along the process order between a suite's products, and never from one suite to another (`make prd-check`, ADR-0041 to ADR-0043).
- In the shell, nothing imports `ag-grid-*` outside the DataGrid wrapper, a module imports from its own layer or a lower one only and never from another suite, every route declares its permission, no colour is given by value, and Prettier's check passes (ADR-0040).

### Documentation

- `docs/prd/` the requirements: IronSaaS Foundation's two PRDs in `foundation/`, the eProcurement Suite's in `suites/procurement/`, each with a CSV beside it, the product method, the map from the archived Functional Foundation PRD, and their README on how to cite and change them (ADR-0041 to ADR-0043)
- `docs/slices/` the slice plans, each with its requirement coverage, its task table and its closing notes
- `docs/adr/` the architecture decision records, one per decision, with an index and a reading order
- `docs/guides/developer-guide.md` how to work on the repository: add a module, an entity, an endpoint with a permission and its checks, a licence, a flag, a setting, a shell page, a provisioning step
- `docs/guides/local-stack.md` the runbook of the laptop platform
- `docs/guides/definition-of-done.md` what a change needs before it is merged
- `docs/design/design-language.md` the brief every page is judged against
- `docs/prompts/` the instruction files Claude Code builds from, one per task
- `docs/STATUS.md` where the work stands, with its open questions and later items
- `CLAUDE.md` the standing rules Claude Code reads on every task

### Versions decided

| Component | Version |
|---|---|
| .NET | 10 (LTS) |
| PostgreSQL | 19 (release candidate image until the GA planned for 2026-11-19) |
| Node.js | 26 |
| React | 19.3 |
| Mantine | 9.7.3 |
| AG Grid | Community 36; Enterprise later by decision, in one file |
| Object storage on the laptop | Azurite (Azure Blob stand-in), see ADR-0006 |
| Secrets on the laptop | OpenBao in server mode on a volume, unsealed by its key in `.env`; kept across restarts (ADR-0016, ADR-0028) |
