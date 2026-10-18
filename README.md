# Foundation

Foundation is the technical base for a family of enterprise SaaS products by IronSaaS. Product teams build business modules on it and get multi-tenancy, identity, permissions, audit, telemetry, a UI shell, provisioning and the operations tooling without writing any of it themselves.

Code namespace: `Iron.*`. Database schemas carry the module names.

The requirements live in `docs/prd/`. The build plan is in slices (PRD section 7). Slice S1, the walking skeleton, is complete: see `docs/slices/S1-walking-skeleton.md` for what it proves, how it was built and what it carries into S2. Slice S2, access control, organisations and the design system, is next: `docs/slices/S2-access-control-organisations-and-design.md`, built with Claude Code from `docs/prompts/S2.md` (ADR-0023) in the design language of ADR-0024.

## What works today

- A tenant is registered through the control plane API and provisioned by the fleet in about fifteen seconds: its own database roles and database, blob container, secrets path and token, migrations, seeds, a Compose unit of three containers (API, worker, shell) behind the ingress, and a readiness check through the ingress. A failed step resumes; an active unit takes new images with an update job; de-provisioning keeps the data.
- A person follows the invitation link to the tenant's own hostname, sets a password, signs in with a server-side session, and sees the pages and navigation the permissions allow. A service account gets a 15-minute token with the client credentials grant.
- Every request carries a correlation id that is its trace id in Tempo; every error shows it; every permission denial, login and provisioning step is in the append-only audit trail.
- 387 .NET tests (architecture rules, unit, and integration on real PostgreSQL, Azurite and OpenBao containers), 91 shell tests, an eleven-step smoke test against the live stack, six browser tests against it and ten without it (the accessibility scan), and a pipeline that builds the images and proves them on the runner.

## The first hour: a fresh checkout to a provisioned tenant and a visible trace

Timings are from a 32 GB WSL 2 laptop with a warm image cache; the first `make images` and `make up` download about 2 GB more.

| Step | Command | Expect | Time |
|---|---|---|---|
| 1 | Prerequisites below | `dotnet --version` 10.0.x, `docker compose version` 2.x, `node --version` v26 from WSL | 10 min |
| 2 | `git clone <repo> ~/foundation && cd ~/foundation` | | 1 min |
| 3 | `make build && make test` | build clean, 387 passed | 5 min |
| 4 | `make up` | platform healthy, the secret store initialised (its root token in `infra/compose/.env`), endpoints printed | 3 min |
| 5 | `make images` | five images `foundation/iron-*:dev` | 10 min |
| 6 | `make secret-control && make migrate-control && make seed-control` | control database and operator | 1 min |
| 7 | terminal 2: `make run-control`; terminal 3: `make run-fleet` | both listening | 1 min |
| 8 | `make provision TENANT=acme` | eight steps, then the invitation link | 1 min |
| 9 | `curl.exe -i http://acme.localhost/health/ready` (from Windows) or the browser | 200 with `X-Correlation-Id` | |
| 10 | Open `http://grafana.localhost`, Explore, Tempo, paste that correlation id as the trace id | the request's spans | 1 min |
| 11 | Open the invitation link, set a password, sign in, open Users | the grid lists the administrator and the smoke client | 2 min |

`make smoke` then proves the whole path from the outside in about a minute. The runbook `docs/guides/local-stack.md` has every command, the map of the stack and the troubleshooting table.

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
make build / make test / make check / make format        # the .NET side
make shell-install / make shell-check / make shell-dev   # the shell (npm ci; lint, tests, build; Vite on :5173)
make up / make up-slim / make down / make reset          # the platform
make images                                              # the five images at tag dev
make run-api / make run-worker                           # the dev tenant from source on :5100 and :5101
make secret-control / migrate-control / seed-control     # the control plane's secret, database and operator
make run-control / make run-fleet                        # the control plane from source on :5200
make up-control / make down-control                      # the control plane as containers instead
make provision TENANT=x / update-unit TENANT=x / deprovision TENANT=x
make smoke / make e2e [TENANT=x] / make integration      # the smoke test, the browser tests, the pipeline's stage
make ef-add MODULE=Identity NAME=AddColumn               # a migration for a module context
```

## Repository layout

```
Foundation.slnx              solution (XML format, edit by hand)
Directory.Build.props        compiler settings for every project
Directory.Packages.props     every package version, once, with its licence
global.json                  SDK band and the test runner
Makefile                     the developer commands
src/
  Iron.Core/  Iron.Data/  Iron.Api/  Iron.Telemetry/  Iron.Security/    shared libraries
  Iron.Authorization/  Iron.Audit/  Iron.Identity/  Iron.Files/        tenant modules
  Iron.Tenancy/  Iron.Fleet/                                            control plane modules
    Api/  Services/  Data/  Contracts/  README.md                       inside every module
  Iron.Host.Api/  Iron.Host.Worker/              tenant unit hosts (+ Dockerfiles)
  Iron.Host.ControlPlane/  Iron.Host.Fleet/      control plane hosts (+ Dockerfiles)
shell/                       the React shell: one build for every tenant (+ Dockerfile, e2e/)
tests/                       test projects; Iron.Architecture.Tests holds the build checks
tools/Iron.Smoke/            the smoke test
infra/compose/               the laptop platform: compose.yaml, configs, scripts, rendered units/
.github/workflows/           ci.yml and release.yml
docs/                        prd/, adr/, guides/, slices/
```

## What the build enforces

- Warnings are errors in every project; `dotnet format` must make no change.
- Every package version is declared once in `Directory.Packages.props`, with its licence; the shell's are in `shell/package.json`.
- Module boundaries: a module may reference shared libraries and `Contracts` projects only. The compiler enforces it through project references; `tests/Iron.Architecture.Tests` names any violation, including type references into another module's `Api`, `Services` and `Data` namespaces.
- No Foundation assembly reads the system clock directly; time comes from an injected `TimeProvider`.
- Every module DbContext passes the model rules and its migrations match its model; the schema check runs over the migrated schema.
- Every endpoint declares a permission, `RequireSignedIn` or `AllowAnonymous`; a host does not start otherwise, and the anonymous list is fixed by a test.
- In the shell, nothing imports `ag-grid-*` outside the DataGrid wrapper, and every route declares its permission.

## Documentation

- `docs/slices/S1-walking-skeleton.md` the slice plan, its requirement coverage, the task table and the closing notes
- `docs/adr/` twenty-two architecture decision records, one per decision, with an index
- `docs/guides/developer-guide.md` how to work on the repository: add a module, an entity, an endpoint with a permission, a shell page, a provisioning step
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
