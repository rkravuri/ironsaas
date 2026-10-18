# Foundation

Foundation is the technical base for a family of enterprise SaaS products by IronSaaS. Product teams build business modules on it and get multi-tenancy, identity, permissions, audit, telemetry, a UI shell and the operations tooling without writing any of it themselves.

Code namespace: `Iron.*`. Database schemas carry the module names.

The requirements live in `docs/prd/`. The build plan is in slices (PRD section 7). Slice S1, the walking skeleton, is in progress: see `docs/slices/S1-walking-skeleton.md`.

## Status

| Task | State |
|---|---|
| T1 Repository skeleton and build checks | done |
| T2 Platform stack with Compose | done |
| T3 Iron.Core | done |
| T4 Iron.Data | done |
| T5 Iron.Api | done |
| T6 Iron.Telemetry and Iron.Security | done |
| T7 Hosts | done |
| T8 Iron.Authorization | done |
| T9 Iron.Audit | done |
| T10 Iron.Identity part 1 | done |
| T11 Iron.Identity part 2 | done |
| T12 Iron.Files and secrets | done |
| T13 Iron.Tenancy and the control plane | done |
| T14 Iron.Fleet and the fleet worker | done |
| T15 Shell foundation | done |
| T16 DataGrid and the Users page | done |
| T17 Smoke and end-to-end tests | done |
| T18 Pipeline | next |

## Prerequisites

- Linux, or WSL 2 with Ubuntu. The runtime is Linux; nothing targets Windows. Clone inside the Linux file system (for example `~/foundation`), not under `/mnt/c`.
- .NET SDK 10.0. Install inside WSL with Microsoft's script:

  ```bash
  sudo apt update && sudo apt install -y libicu-dev make git
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh && /tmp/dotnet-install.sh --channel 10.0
  cat >> ~/.bashrc <<'EOT'
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$PATH:$HOME/.dotnet:$HOME/.dotnet/tools"
  EOT
  source ~/.bashrc && dotnet --version
  ```

  `global.json` accepts any 10.0.1xx feature band. A system-wide install through `ppa:dotnet/backports` works too.
- Docker with the Compose plugin: Docker Engine inside WSL, or Docker Desktop with WSL integration. `docker compose version` prints 2.x.
- From T15: Node.js v26.12.0 (see `.node-version`).

## Quick start

```bash
make build     # build every project; warnings are errors
make test      # run every test project (the data tests start a PostgreSQL container, so Docker must be running)
make check     # run only the architecture checks
make up        # start the laptop platform (PostgreSQL, blob storage, OpenBao, Traefik, telemetry) and wait until healthy
make down      # stop it; data is kept
make run-api   # run the tenant API host on http://localhost:5100 against the platform
make run-worker # run the tenant worker host on http://localhost:5101 (heartbeat every 15 s)
make migrate   # run the migrate command against the laptop database
make seed      # run the seed command: system roles, the first administrator (invitation token in the log) and the smoke client
make token     # get an access token for the laptop smoke client from the running API host
make secret-dev      # write the dev unit's secret to OpenBao, as the fleet does per tenant
make run-api-secrets # run the API with only the store address, the unit token and the tenant in its environment
make secret-control && make migrate-control && make seed-control   # the control plane's secret, database and operator
make run-control             # the control plane API on http://localhost:5200
make run-fleet               # the fleet worker with the Compose runtime (drives docker on this machine)
make provision TENANT=acme   # register a tenant, provision it, follow the steps, print the invitation link
make deprovision TENANT=acme # take the unit down; data is kept
make smoke                   # the smoke test: provisions or reuses tenant smoke, token, cookie, denials, trace in Tempo
make e2e                     # Playwright: login and the Users grid on the smoke tenant (browsers: cd shell && npm run e2e:install)
make images    # build the four host images foundation/iron-*:dev
make ef-add MODULE=Authorization NAME=Initial   # generate a migration for a module context (dotnet ef via the tool manifest)
```

`make` with no target lists every command. The platform runbook is `docs/guides/local-stack.md`. After `make up`, Grafana is at http://grafana.localhost and the Traefik dashboard at http://traefik.localhost.

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
  Iron.Host.Api/  Iron.Host.Worker/              tenant unit hosts
  Iron.Host.ControlPlane/  Iron.Host.Fleet/      control plane hosts
  Iron.Shell/                React shell and design system (T15)
tests/                       Iron.Architecture.Tests holds the build checks
infra/compose/               the laptop platform: compose.yaml, configs, scripts
products/  tools/            products and developer tools (later tasks)
docs/                        prd/, adr/, guides/, slices/
```

## What the build enforces today

- Warnings are errors in every project.
- Every package version is declared once in `Directory.Packages.props`, with its licence.
- Module boundaries: a module may reference shared libraries and `Contracts` projects only. The compiler enforces it through project references; `tests/Iron.Architecture.Tests` names any violation.
- Another module's `Api`, `Services` and `Data` namespaces are private. The architecture tests read the compiled assemblies and fail on any type reference into them.
- Every project sits in a folder with its own name; every module has a `Contracts` project inside it.
- No Foundation assembly reads the system clock directly; time comes from an injected `TimeProvider` (ADR-0007).
- Every module DbContext passes the model rules: entities derive from the Foundation base classes and business entities carry an owning organisation (ADR-0008).

## Try the boundary check

1. Open `src/Iron.Audit/Iron.Audit.csproj`.
2. Add `<ProjectReference Include="../Iron.Identity/Iron.Identity.csproj" />` to the item group.
3. Run `make check`. The test `EveryProjectReferenceFollowsTheModuleBoundaryRules` fails and names `src/Iron.Audit/Iron.Audit.csproj`.
4. Remove the line again.

## Documentation

- `docs/slices/` the slice plans; S1 is the current one and the brief for every build session
- `docs/adr/` architecture decision records; every decision gets one as it is built
- `docs/guides/developer-guide.md` how to work on the repository and add a module
- `docs/guides/local-stack.md` the runbook of the laptop platform
- `docs/prd/` the Technical Foundation PRD and the Functional Foundation PRD

## Versions decided for Foundation

| Component | Version |
|---|---|
| .NET | 10 (LTS) |
| PostgreSQL | 19 (release candidate image until the GA planned for 2026-11-19) |
| Node.js | v26.12.0 |
| React | 19.3 |
| Mantine | 9.7.3 |
| AG Grid | Community edition; Enterprise later by decision |
| Object storage on the laptop | Azurite (Azure Blob stand-in), see ADR-0006 |
