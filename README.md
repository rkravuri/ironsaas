# Foundation

Foundation is the technical base for a family of enterprise SaaS products by IronSaaS. Product teams build business modules on it and get multi-tenancy, identity, permissions, audit, telemetry, a UI shell, provisioning and the operations tooling without writing any of it themselves.

Code lives in three layers (ADR-0029): Kernel, the technical mechanisms (`Iron.Kernel`, `Iron.Kernel.*`); Business, the shared business masters (`Iron.Business.*`); Products, the documents and processes with their own life cycles (`Iron.Products.*`). Database schemas carry the layer prefix and the module name: `kernel_identity`.

The requirements live in `docs/prd/`. The build plan is in slices (PRD section 7). Slice S1, the walking skeleton, and slice S2, access control, organisations and the design system, are complete: each plan in `docs/slices/` says what the slice proves, how it was built and what it carries into the next. S2 was built with Claude Code from `docs/prompts/S2.md` (ADR-0023) in the design language of ADR-0024. Slice S3, trust and compliance, is next: `docs/slices/S3-trust-and-compliance.md` holds its requirements, and its tasks are written in the design session.

## What works today

- A tenant is registered through the control plane API and provisioned by the fleet in about fifteen seconds: its own database roles and database, blob container, secrets path and token, migrations, seeds, a Compose unit of three containers (API, worker, shell) behind the ingress, and a readiness check through the ingress. A failed step resumes; an active unit takes new images with an update job; de-provisioning keeps the data.
- A person follows the invitation link to the tenant's own hostname, sets a password, signs in with a server-side session, and sees the pages and navigation the permissions allow. A service account gets a 15-minute token with the client credentials grant.
- Every request carries a correlation id that is its trace id in Tempo; every error shows it; every permission denial, login and provisioning step is in the append-only audit trail.
- A tenant administrator shapes who may do what: an organisation tree, tenant roles made of permissions and granted at the whole tenant, a unit with or without the units below it, or a record; record policies that lists and reads apply; delegation of duties with both names in the audit trail; access reviews; records shared with listed units, with every read across units audited; a choice of the unit a person works for; units created, moved, merged and deactivated; an explanation of why a person can or cannot do something; and conflict rules that block or warn. The control plane decides what a tenant has, by plan, module, feature and seat, and which flags and kill switches apply; the tenant's own settings resolve from a person through the units to the tenant. Every change reaches the API and the shell within a minute.
- The shell draws every page in the design language (ADR-0024): a home page with an opener per module, search with Ctrl+K, help, the session warning, the console's pages, light and dark schemes, two densities, a mark of its own per tenant, keyboard operation, and WCAG 2.1 AA on every page in the gallery.
- 758 .NET tests (architecture rules, unit, and integration on real PostgreSQL, Azurite and OpenBao, one of each for the whole run), 229 shell tests, a fourteen-step smoke test against the live stack, 25 browser tests against it and 60 without it (the accessibility scan), and a pipeline that builds the images and proves them on the runner.

## The first hour: a fresh checkout to a provisioned tenant and a visible trace

Timings are from the reference machine, a laptop with 16 GB of memory and WSL 2 limited to 12 GB with 8 GB of swap (`.wslconfig`, see the runbook's prerequisites), with a warm image cache; the first `make images` and `make up` download about 2 GB more.

| Step | Command | Expect | Time |
|---|---|---|---|
| 1 | Prerequisites below | `dotnet --version` 10.0.x, `docker compose version` 2.x, `node --version` v26 from WSL | 10 min |
| 2 | `git clone <repo> ~/foundation && cd ~/foundation` | | 1 min |
| 3 | `make build && make test` | build clean, 758 passed (Docker must run: the tests start their own PostgreSQL, Azurite and OpenBao) | 5 min |
| 4 | `make up` | platform healthy, the secret store initialised (its root token in `infra/compose/.env`), endpoints printed | 3 min |
| 5 | `make images` | five images `foundation/iron-*:dev` | 10 min |
| 6 | `make secret-control && make migrate-control && make seed-control` | control database and operator | 1 min |
| 7 | terminal 2: `make run-control`; terminal 3: `make run-fleet` | both listening | 1 min |
| 8 | `make provision TENANT=acme` | eight steps, then the invitation link; the tenant starts on the `standard` plan | 1 min |
| 9 | `curl.exe -i http://acme.localhost/health/ready` (from Windows) or the browser | 200 with `X-Correlation-Id` | |
| 10 | Open `http://grafana.localhost`, Explore, Tempo, paste that correlation id as the trace id | the request's spans | 1 min |
| 11 | Open the invitation link, set a password, sign in | the home page: an opener for Identity and one for the console, each with its pages; Ctrl+K searches them | 2 min |
| 12 | Open Users from the Identity opener | the grid lists the administrator and the smoke client | 1 min |

`make smoke TENANT=acme` then proves the whole path from the outside in about three minutes, the role scoped to a unit, the plan change and the kill switch included; `make apply-plan TENANT=acme PLAN=Enterprise` gives the tenant role export and import. The runbook `docs/guides/local-stack.md` has every command, the map of the stack and the troubleshooting table.

## Prerequisites

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

## Commands

`make` with no target lists every command with a line of help. The ones used daily:

```bash
make build / make test / make check                     # the .NET side (make test starts its own PostgreSQL, Azurite, OpenBao)
make format                                              # dotnet format and Prettier (ADR-0040)
make shell-install / make shell-check / make shell-dev   # the shell (npm ci; lint, format check, tests, build; Vite on :5173)
make shell-a11y                                          # the accessibility scan of the gallery, no stack needed
make up / make up-slim / make down / make reset          # the platform
make images                                              # the five images at tag dev
make run-api / make run-worker                           # the dev tenant from source on :5100 and :5101
make secret-control / migrate-control / seed-control     # the control plane's secret, database and operator
make run-control / make run-fleet                        # the control plane from source on :5200
make up-control / make down-control                      # the control plane as containers instead
make provision TENANT=x / update-unit TENANT=x / deprovision TENANT=x
make apply-plan TENANT=x PLAN=y [DRY_RUN=1]              # preview a plan for a tenant, then apply it
make flag KEY=k VALUE=v [TENANT=x]                       # a flag's rule for a tenant or the platform
make kill-switch PATH=imports ON=true REASON="..."       # a kill switch, for the platform or TENANT=x
make catalogue                                           # what the units can license, switch and set, for the control plane
make smoke / make e2e [TENANT=x] / make integration      # the smoke test, the browser tests, the pipeline's stage
make ef-add MODULE=Identity NAME=AddColumn               # a migration for a module context (the last segment of its name)
```

## Repository layout

```
Foundation.slnx              solution (XML format, edit by hand)
Directory.Build.props        compiler settings for every project
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
  Business/                  shared business masters (empty until the Business PRD; README)
  Products/                  products with their own documents and life cycles (empty; README)
  Hosts/
    Iron.Host.Api/  Iron.Host.Worker/            tenant unit hosts (+ Dockerfiles)
    Iron.Host.ControlPlane/  Iron.Host.Fleet/    control plane hosts (+ Dockerfiles)
shell/                       the React shell: one build for every tenant (+ Dockerfile, e2e/);
                             modules in src/modules/kernel, business and products
tests/                       Iron.Architecture.Tests (the build checks), Iron.Host.Tests, Kernel/ with a test
                             project per kernel project, and Iron.Testing (the services the tests run on)
tools/Iron.Smoke/            the smoke test
infra/compose/               the laptop platform: compose.yaml, configs, scripts, rendered units/
.github/workflows/           ci.yml and release.yml
docs/                        prd/, adr/, guides/, slices/
```

## What the build enforces

- Warnings are errors in every project; `dotnet format` must make no change.
- Every package version is declared once in `Directory.Packages.props`, with its licence; the shell's are in `shell/package.json`.
- Layers: every project declares `<IronLayer>` (the build stops without one), and references go downward only: Products may use Business and Kernel, Business may use Kernel, Kernel uses only Kernel (ADR-0029).
- Module boundaries: a module may reference the kernel vocabulary, the kernel libraries and `Contracts` projects only. The compiler enforces it through project references; `tests/Iron.Architecture.Tests` names any violation, including type references into another module's `Api`, `Services` and `Data` namespaces.
- No Foundation assembly reads the system clock directly; time comes from an injected `TimeProvider`.
- Every module DbContext passes the model rules, its schema carries its layer's prefix, and its migrations match its model; the schema check runs over the migrated schema.
- Every endpoint declares a permission, `RequireSignedIn` or `AllowAnonymous`; a host does not start otherwise, and the anonymous list is fixed by a test.
- In the shell, nothing imports `ag-grid-*` outside the DataGrid wrapper, a module imports from its own layer or a lower one only, every route declares its permission, no colour is given by value, and Prettier's check passes (ADR-0040).

## Documentation

- `docs/slices/` the slice plans, each with its requirement coverage, its task table and its closing notes; S3's is a skeleton
- `docs/adr/` forty architecture decision records, one per decision, with an index and a reading order
- `docs/guides/developer-guide.md` how to work on the repository: add a module, an entity, an endpoint with a permission and its checks, a licence, a flag, a setting, a shell page, a provisioning step
- `docs/guides/local-stack.md` the runbook of the laptop platform
- `docs/guides/definition-of-done.md` what a change needs before it is merged
- `docs/design/design-language.md` the brief every page is judged against
- `docs/prompts/` the instruction files Claude Code builds from, one per slice
- `CLAUDE.md` the standing rules Claude Code reads on every task
- `docs/prd/` the Technical Foundation PRD and the Functional Foundation PRD

## Versions decided for Foundation

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
