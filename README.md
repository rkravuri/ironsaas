# IronSaaS Foundation

IronSaaS Foundation is a multi-tenant SaaS platform for regulated industries: one technical and business base on which IronSaaS builds suites of products. Every tenant has the Foundation: tenancy, identity, permissions and organisations, audit, entitlements, flags and settings, telemetry, provisioning, a UI shell in one design language, and the business masters that enterprise products share.

The first suite is the **eProcurement Suite**: source to pay, from supplier management through requests, orders and receiving to invoicing, with one portal for suppliers. Other suites can follow on the same Foundation, a School ERP among them, each with its own products and the masters only its products share (ADR-0043). Everything is built for regulated industries: audit, access control and electronic records are platform requirements, not features a product adds.

## Where it stands

| Part | What it is | Status | Requirements | Slices |
|---|---|---|---|---|
| Kernel | The technical mechanisms: tenancy and the fleet with suites per tenant, identity, authorization and organisations, content visibility, audit, entitlements, flags, settings, files, telemetry, the shell | Built in part: S1, S2, S3 and V are done, S4 is under way | [Technical Foundation PRD](docs/prd/foundation/technical-foundation-PRD.md) | [S1](docs/slices/S1-walking-skeleton.md), [S2](docs/slices/S2-access-control-organisations-and-design.md), [S3](docs/slices/S3-trust-and-compliance.md) and [V](docs/slices/V-visibility-and-suites.md); [S4](docs/slices/S4-platform-services.md) as a skeleton; S5 to S9 in the PRD's section 7.1 |
| Business masters | Organisations and legal entities, people, items, finance and budgets, locations, the supplier master, lookups, approvals and the delegation of authority, numbering and templates | Specified | [Business Foundation PRD](docs/prd/foundation/business-foundation-PRD.md) | B1 to B4 in the technical PRD's section 7.2 |
| eProcurement Suite: declared | The suite in code (`procurement`), installed only for the tenants that bought it, with its shared master Purchasing as a skeleton: one table that records the installation, and an About page that shows what the tenant has; no business data yet | Declared: installed per tenant since V2; Purchasing's masters come with PR0 | the suite's [module list](docs/prd/suites/procurement/README.md); Purchasing's [PRD](docs/prd/suites/procurement/purchasing-PRD.md) | [V](docs/slices/V-visibility-and-suites.md) for the skeleton; PR0 in the technical PRD's section 7.2 |
| eProcurement Suite: products | Supplier management, Contracts, Catalogue, Requests, Orders, Receiving, Invoicing | Specified | one PRD each in [`docs/prd/suites/procurement/`](docs/prd/suites/procurement/) | PR1 to PR7 in the technical PRD's section 7.2 |
| eProcurement Suite: supplier portal | One portal for all the suppliers of all customers, on the kernel's Supplier Network | Specified | [Supplier portal PRD](docs/prd/suites/procurement/supplier-portal-PRD.md) | PR8, after the kernel's S8 |
| eProcurement Suite: later modules | Spend Analytics and Savings, Sourcing with auctions, Category and Savings Projects, Supplier Risk and Sustainability (wave 2); Payments, Services and Contingent Workforce, Expenses (wave 3) | Scoped: one page each | the [module list](docs/prd/suites/procurement/README.md) and the `*-scope.md` pages beside it | waves 2 and 3, after PR7 in the technical PRD's section 7.2 |
| Other suites | A School ERP and others | Not started | | |

Built means it runs and its tests pass; declared means it exists in code and is installed, without its function yet; specified means its requirements are written and accepted; scoped means one page says what it will be. Content visibility (TF-241) and suites per tenant (TF-244) are built (slice V), before the first business slice.

## How a customer buys

A customer is a tenant with a unit of its own: its own database, storage container, secrets and containers. Every tenant has IronSaaS Foundation, with a Foundation plan that says which business modules and kernel features it has; today those plans are Standard and Enterprise. On top, a tenant buys one or more suites, each with a plan of its own modules and features, and a suite is installed only for the tenants that bought it (TF-244). Plans, entitlements, seats and a change of plan that reaches the tenant within a minute are built (ADR-0034), and so are suites per tenant (ADR-0059): the operator installs a suite for a tenant with a reason, as an update job, and removing it keeps its data. The eProcurement Suite's one plan today is eProcurement Essentials, with Purchasing.

## Compliance

Five frameworks are in scope for every tenant (the technical PRD's section 5a): SOC 2 Type II, ISO 27001, GDPR, India's DPDP Act, and 21 CFR Part 11 with EU Annex 11. No certification is claimed; the order of certification is an open operational decision. The requirements also follow GAMP 5 (Second Edition) for risk-based validation, with IronSaaS normally assessed as category 4, and ALCOA+ for the integrity of records; section 5a maps each principle to the requirements that implement it.

Built today:

- Every endpoint declares its permission, and access is denied by default. Roles are granted at the tenant, a unit or a record; record policies, sharing across units, visibility tags that narrow which records each person sees, delegation with both names on record, access reviews, conflict rules for duties one person must not hold together, and an explanation of every decision.
- Sign-in under a password policy with lockout, a second factor (an authenticator app or a passkey) required by the tenant's policy, single sign-on through OpenID Connect, a recent sign-in before every risky action, server-side sessions a person or an administrator ends, and revocation within a minute.
- Impersonation that names both people on every event and is shown to the person; break-glass only with a ticket and a second operator, reviewed afterwards; terms accepted per version.
- An audit trail of sign-ins, denials, provisioning steps and every state change, written in the same transaction as the change, with each changed field; sealed into a chain per tenant, anchored outside the unit and verified daily; searched, exported, archived after 13 months and kept by retention classes and legal holds. Every time is the server's.
- Electronic signatures bound to the version they sign, with their manifestation, and signed reviews of the audit trail (21 CFR Part 11); evidence packs per period; a second administrator's approval of sensitive configuration; a validation pack per release, with the matrix of what each test proves and a bill of materials per image.
- Each tenant isolated in its own database, database roles, storage container, secrets path and keys; chosen fields encrypted with the tenant's key and still found by their exact value; keys and passwords rotated without a restart; TLS 1.2 and up.
- Security headers with no inline code, a declared limit on every request field, rich text cleaned, SQL from strings refused by the build, rate limits and a proof of work after failed sign-ins.
- Security alert rules over the audit trail every minute, sent to the operator's alerting, and incidents run by the runbook to the 72-hour notice and the review (`docs/security/incident-response.md`).
- Code reaches main only through a pull request with every check green; dependencies, licences, images, misconfigurations and our own rules are scanned through one gate with expiring exceptions; coverage may not fall; every module has its threat model (ADR-0055).

## What works today

**Tenants and operations**

- The control plane registers a tenant and the fleet provisions it in about fifteen seconds: its database roles and database, storage container, secrets path and token, migrations, seeds, and a unit of three containers (API, worker, shell) behind the ingress, checked through the ingress. A failed step resumes; an update job gives an active unit new images; de-provisioning keeps the data.
- The control plane decides what each tenant has, by plan (one for the Foundation and one per suite), module, feature and seat, and which flags and kill switches apply; a change reaches the tenant's API and shell within a minute.

**People and access**

- A person follows an invitation to the tenant's own hostname, sets a password and signs in with a server-side session; a service account gets a 15-minute token with the client credentials grant.
- A tenant administrator shapes who may do what: the organisation tree, roles and their scopes, record policies, shared records, delegation, access reviews, the unit a person works for, units created, moved, merged and deactivated, conflict rules, and why a person can or cannot do something.
- The tenant's settings resolve from a person through the units to the tenant.

**Sign-in and sessions** (S3)

- A person signs in with a password, then a second factor when the tenant's policy requires one (an authenticator app or a passkey, with ten recovery codes), or through the tenant's company login; risky actions ask for a recent sign-in again.
- A person sees and ends their own sessions and their security activity in My profile; an administrator ends any session with a reason, unlocks a login, issues a one-time recovery link, resets a lost device's factors, or revokes everything of a person or the tenant.
- The operator recovers a tenant's administrator, signs in as a person where the tenant allows it, or opens break-glass access with a second operator, each with a reason both audit trails keep.

**Records and evidence** (S3)

- The audit trail's search, export, chain status, archive, retention and legal holds; each record's History; signatures with their block on screen and in print; signed reviews of the trail; evidence packs; pending changes with before and after; the installed version and its validation pack.

**Security operations** (S3)

- The console's Keys, Security alerts and Alert rules pages; the operator's key rotations, audit verifications, alerts and incidents from the make targets in the runbook.

**Visibility and suites** (V)

- A tenant administrator defines visibility tags and gives them to people; a record carries tags, and a person who holds tags sees only the records that share one with them or carry none, in lists and by identifier, with the explanation of access and the access reviews showing the tags.
- The operator installs a suite for a tenant that bought it, when it is registered or later, and removes it again keeping its data, each with a reason and an update job; a unit loads only the Foundation and its tenant's suites, and a tenant with the eProcurement Suite has its group on the home page and its About page.

**Platform services** (S4)

- A module tells others of a change through a domain event, written in the change's own transaction and listed in the catalogue; the unit's worker hands it to each subscriber within a second, once, in order per record, retrying a failure and parking it as an alerted dead letter. Deactivating a person ends their delegations, and an ended delegation stops the delegate acting at once. The console's Domain events page searches the events and their dead letters and replays them, with a reason, as the operator can through the control plane (ADR-0060).

**The shell**

- One React build for every tenant, in the design language of ADR-0024: a home page that opens each module, grouped by suite with the Foundation's first; search with Ctrl+K; help; the session warning; the console's pages; light and dark schemes and two densities; a mark of its own per tenant; keyboard operation; WCAG 2.1 AA on every page of the gallery.

**Checks**

- The architecture tests hold the layers, the suites and the module boundaries; the requirement documents are checked on every change; the tests run on real PostgreSQL, Azurite and OpenBao, and each names the criteria or the decisions it proves, which the traceability matrix checks (ADR-0044); a smoke test and browser tests run against the live stack; every pull request runs the checks and the scans, and the nightly release builds the images, proves them and publishes their validation pack.

## Licence

Proprietary. Copyright IronSaaS, all rights reserved. The licences of third-party fonts, images and data are in [`docs/licences.md`](docs/licences.md); every package's licence is beside its version in `Directory.Packages.props` and `shell/package.json`.

To report a vulnerability, see [SECURITY.md](SECURITY.md).

## How we work

The platform is built in slices, each a thin increment that can be demonstrated end to end (the technical PRD's section 7). A design session writes each task as a prompt in `docs/prompts/`; Claude Code builds it on a branch of its own, commits the prompt before the work, opens a pull request and reports with the checks, the screenshots and the answers to the design brief's questions; the design session then accepts it, and it merges through the pull request with every check green, or answers with corrections (ADR-0023, ADR-0055). Main takes no direct push. Every decision with real alternatives is an ADR, every requirement has an identifier that its tests name, and the build refuses what the rules forbid.

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
| 11 | Open the invitation link, set a password, sign in, and set up a second factor: the MFA policy requires one of a tenant administrator, so "Set up a second factor" comes before any page. Choose an authenticator app (scan the code, type its six digits) or a passkey, then keep the ten recovery codes | the home page: under IronSaaS Foundation, openers for Identity, Audit, Compliance and the console, each with its pages; Ctrl+K searches them | 4 min |
| 12 | Open Users from the Identity opener | the grid lists the administrator and the smoke client | 1 min |

`make smoke` then proves the whole path from the outside in a few minutes, on a tenant of its own called `smoke`, which it provisions on its first run: it sets its administrator's password and second factor itself, so point it with `TENANT=` only at a tenant whose administrator nobody uses by hand. `make apply-plan TENANT=acme PLAN=Enterprise` gives the tenant role export and import. The runbook `docs/guides/local-stack.md` has every command, the operator's included, the map of the stack and the troubleshooting table.

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
make provision TENANT=x [SUITES=procurement REASON="..."] / update-unit TENANT=x / deprovision TENANT=x
make suite-add TENANT=x SUITE=procurement REASON="..."   # a suite for an active tenant, and suite-remove (ADR-0059)
make apply-plan TENANT=x PLAN=y [DRY_RUN=1]              # preview a plan for a tenant, then apply it
make sample-records TENANT=x [ON=0]                      # the sample module in a test tenant's unit (ADR-0057)
make flag KEY=k VALUE=v [TENANT=x]                       # a flag's rule for a tenant or the platform
make kill-switch PATH=imports ON=true REASON="..."       # a kill switch, for the platform or TENANT=x
make events TENANT=x [STATUS=dead] / events-replay ...   # a tenant's domain events, and a replay to one subscriber (ADR-0060)
make catalogue                                           # what the units can license, switch and set, and the events they publish, grouped by suite
make prd-csv / make prd-check                            # the requirement documents' CSVs, and their check
make smoke / make e2e [TENANT=x] / make integration      # the smoke test, the browser tests, the pipeline's stage
make trace / make security / make coverage               # the traceability matrix, the security gate, the coverage floors (ADR-0044, ADR-0055)
make verify                                              # every check in order, as before a pull request
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
    Iron.Kernel.Compliance/  .Changes/  .Protection/  .Keys/  .Monitoring/   tenant modules (S3)
    Iron.Kernel.Visibility/  .Samples/                                tenant modules (V; Samples in test units only)
    Iron.Kernel.Tenancy/  .Fleet/                                     control plane modules
      Api/  Services/  Data/  Contracts/  README.md                   inside every module
  Business/                  shared business masters (empty until the first business slice; README)
  Suites/                    one folder per suite (ADR-0043; README)
    Procurement/             the eProcurement Suite: Iron.Suites.Procurement.*, its skeleton Purchasing since V2 (README)
  Hosts/
    Iron.Host.Api/  Iron.Host.Worker/            tenant unit hosts (+ Dockerfiles)
    Iron.Host.ControlPlane/  Iron.Host.Fleet/    control plane hosts (+ Dockerfiles)
shell/                       the React shell: one build for every tenant (+ Dockerfile, e2e/, lint/ for its own rule);
                             modules in src/modules/kernel, business and suites/<suite>
tests/                       Iron.Architecture.Tests (the build checks), Iron.Host.Tests, Kernel/ with a test
                             project per kernel project, and Iron.Testing (the services the tests run on)
tools/                       Iron.Smoke (the smoke test), Iron.Analyzers (the build's own rules), Iron.ProcessMaps,
                             and the Python checks: prd_check.py, trace.py, security_gate.py, coverage_check.py and others
infra/compose/               the laptop platform: compose.yaml, configs, scripts, rendered units/
.github/workflows/           ci.yml (every pull request), release.yml (nightly and on demand), security-report.yml (monthly)
security/                    the security gate's exceptions, licence policy, coverage floors and the repository's own scan rules
docs/                        prd/ (foundation/, suites/procurement/), adr/, guides/, slices/, prompts/, security/, trace/, risk/
```

### What the build enforces

- Warnings are errors in every project; `dotnet format` must make no change.
- Every package version is declared once in `Directory.Packages.props`, with its licence; the shell's are in `shell/package.json`.
- Layers and suites: every project declares `<IronLayer>`, and a suite's project its `<IronSuite>` (the build stops without them). References go downward only: a suite may use Business and Kernel, Business may use Kernel, Kernel uses only Kernel; a suite never references another suite (ADR-0029, ADR-0043).
- Module boundaries: a module may reference the kernel vocabulary, the kernel libraries and `Contracts` projects only. The compiler enforces it through project references; `tests/Iron.Architecture.Tests` names any violation, including type references into another module's `Api`, `Services` and `Data` namespaces.
- No Foundation assembly reads the system clock directly; time comes from an injected `TimeProvider`.
- Every module DbContext passes the model rules (a business record is tracked and its table keeps its visibility tags' column), its schema carries its layer's prefix or its suite's short name, and its migrations match its model; the schema check runs over the migrated schema.
- Every image is pinned by its version and its digest (ADR-0055), and every tracked shell script is executable.
- Every endpoint declares a permission, `RequireSignedIn` or `AllowAnonymous`; a host does not start otherwise, and the anonymous list is fixed by a test.
- The requirement documents: every identifier once, every old identifier in the map, criteria in the Given, When, Then form with an error case, each CSV equal to its markdown, and dependencies down the layers, along the process order between a suite's products, and never from one suite to another (`make prd-check`, ADR-0041 to ADR-0043).
- In the shell, nothing imports `ag-grid-*` outside the DataGrid wrapper, a module imports from its own layer or a lower one only and never from another suite, every route declares its permission, no colour is given by value, HTML is rendered in one component, and Prettier's check passes (ADR-0040, ADR-0053).
- Every test names the criteria or the decisions it proves, and the matrix fails a done slice's criterion without a test or an approved exception (ADR-0044).
- Every request field has a declared limit, and SQL is never made from strings (IRON0010, IRON0011); every .NET security rule and ESLint's security plugin are errors in the product's code (ADR-0053, ADR-0055).
- A critical or high finding of a dependency, a licence, an image, a misconfiguration or our own rules fails the build unless an unexpired exception covers it; line coverage may not fall below its floor; a module or an anonymous endpoint without its threat model fails the tests (ADR-0055).

### Documentation

- `docs/prd/` the requirements: IronSaaS Foundation's two PRDs in `foundation/`, the eProcurement Suite's in `suites/procurement/`, each with a CSV beside it, the product method, the map from the archived Functional Foundation PRD, and their README on how to cite and change them (ADR-0041 to ADR-0043)
- `docs/slices/` the slice plans, each with its requirement coverage, its task table and its closing notes
- `docs/adr/` the architecture decision records, one per decision, with an index and a reading order
- `docs/guides/developer-guide.md` how to work on the repository: add a module, an entity, an endpoint with a permission and its checks, a licence, a flag, a setting, a shell page, a provisioning step
- `docs/guides/local-stack.md` the runbook of the laptop platform
- `docs/guides/definition-of-done.md` what a change needs before it is merged
- `docs/security/` the security policy, the threat models, the process exceptions, the findings register, main's protection and the incident runbook
- `docs/trace/` the exceptions to the traceability matrix and the risk classes; `docs/risk/` each task's risk section, which the validation pack quotes
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
