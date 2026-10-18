# Runbook: the laptop platform

Everything Foundation needs runs on the developer laptop in containers (PRD section 3.3, decision D-14, ADR-0005): the platform services, the control plane, and one Compose project per tenant unit. This runbook is the reference for starting, reaching, changing and resetting all of it. The first hour from a fresh checkout is in the README; this page is what you come back to.

## 1. Prerequisites

- WSL 2 with Ubuntu, Docker (Engine inside WSL, or Docker Desktop with WSL integration), `docker compose version` 2.x.
- .NET SDK 10 and Node.js 26, both inside WSL. `which npm` must not point under `/mnt/c`: the Windows npm runs through `cmd.exe`, which cannot work from a WSL path, and installs Windows binaries into `node_modules`.
- Ports 80, 443, 5432, 8200, 10000, 3200, 4317 and 4318 free; 5100, 5101 and 5200 free when the hosts run from source.
- Memory: about 2.5 GB for the platform with telemetry, about 0.5 GB per tenant unit, about 1 GB for the control plane as containers. The reference machine is a laptop with 16 GB of memory, with WSL limited to 12 GB and 8 GB of swap.
- WSL's memory and swap, in `%UserProfile%\.wslconfig` on Windows, under `[wsl2]`, applied after `wsl --shutdown`: on the reference machine `memory=12GB` and `swap=8GB`; on a 32 GB machine `memory=20GB` and `swap=8GB`. `memory` must stay below the machine's own memory. Without the file WSL takes half the machine's memory and a quarter of that as swap, which is too little: measured on 2026-10-19 on the reference machine with those defaults (7.4 GB and 2 GB of swap), with the platform, telemetry, the control plane as containers and four units up, `make test`, `make shell-check`, `make shell-a11y` and `make format` passed, but memory use peaked at 6.9 GB and the swap was full. That is the pressure under which WSL restarted on 2026-10-18 and took the old in-memory store with it.
- Optional: `mkcert` (`make certs`), Playwright browsers (`cd shell && npm run e2e:install`).

## 2. Commands

| Command | What it does |
|---|---|
| `make up` | Creates `infra/compose/.env` on first use, starts every platform container with telemetry, initialises the secret store on first use (section 4), waits until healthy, prints the endpoints |
| `make up-slim` | The platform without the telemetry stack |
| `make up-ci` | Slim plus the collector and Tempo: what the pipeline uses |
| `make down` / `make reset` | Stop and remove the containers (data and secrets kept) / also delete every data volume, the store's included, with the store's root token and recovery key in `.env` |
| `make ps`, `make logs`, `make logs SERVICE=postgres` | Status and logs |
| `make hosts`, `make certs` | The `/etc/hosts` line for command line tools in WSL; a trusted TLS certificate |
| `make images` | The five images at tag `dev` (`TAG=x` for another) |
| `make secret-dev`, `make run-api`, `make run-api-secrets`, `make run-worker`, `make migrate`, `make seed`, `make token` | The dev tenant from source, against the platform database |
| `make secret-control`, `make migrate-control`, `make seed-control`, `make run-control`, `make run-fleet` | The control plane from source |
| `make up-control`, `make down-control` | The control plane as containers from the images |
| `make provision TENANT=x [ADMIN=a@b]`, `make update-unit TENANT=x`, `make deprovision TENANT=x` | Tenant units through the control plane |
| `make smoke [TENANT=x]`, `make e2e [TENANT=x]`, `make integration` | The smoke test and the browser tests against the `smoke` tenant or `TENANT`, the pipeline's integration stage |

## 3. The map

| Service | Address | Credentials |
|---|---|---|
| Traefik dashboard | http://traefik.localhost | none |
| PostgreSQL 19 | `localhost:5432`, database `postgres` (platform), `control`, one per tenant | `POSTGRES_SUPERUSER` and its password in `infra/compose/.env` |
| Blob storage (Azurite) | `http://localhost:10000/<account>`, one container per tenant | `BLOB_ACCOUNT_NAME`, `BLOB_ACCOUNT_KEY` in `.env` |
| OpenBao | http://localhost:8200 and http://openbao.localhost, mount `secret` | `OPENBAO_ROOT_TOKEN` in `.env`, written by the first `make up`; `OPENBAO_UNSEAL_KEY` and `OPENBAO_RECOVERY_KEY` beside it |
| Grafana | http://grafana.localhost | viewer without login; `admin` with `GRAFANA_ADMIN_PASSWORD` |
| Prometheus, Tempo, Loki | http://prometheus.localhost, http://tempo.localhost (`localhost:3200`), `localhost:3100` | none |
| OTLP for hosts on the laptop | `localhost:4317` (gRPC), `localhost:4318` (HTTP) | none |
| Control plane API | http://localhost:5200 (from source or as container), http://control.localhost (container) | operator client credentials, `DEV_OPERATOR_SECRET` in the Makefile |
| A tenant unit | `http(s)://<slug>.localhost`: `/api`, `/auth`, `/connect`, `/health`, `/.well-known` to its API, everything else to its shell | the tenant's accounts |

Inside the Docker network `foundation` the services are `postgres:5432`, `blob:10000`, `openbao:8200`, `otel-collector:4317`, `tempo:3200`, `loki:3100`, `prometheus:9090`, `traefik:80`. Tenant units are separate Compose projects `tenant-<slug>` that join this network; `docker compose ls` lists them.

Hostnames: browsers resolve every `*.localhost` name to the laptop by themselves. Command line tools in WSL do not; run `make hosts` once and add the line to `/etc/hosts`, or call `http://localhost` with a `Host:` header, as the scripts do.

TLS: the `websecure` entry point has TLS on. Without `make certs` Traefik uses its own self-signed certificate, which the browser warns about once; with mkcert, trust its root on Windows as the script explains. `http://` works too, and Chrome treats `*.localhost` as secure, so the session cookie is accepted there as well.

## 4. Secrets

Every unit, the control plane included, gets its configuration at start from one secret in OpenBao: `secret/<path>/unit` with keys that are configuration keys (`ConnectionStrings:Database`, `Storage:ContainerSasUrl`, `Tokens:SigningKeyPem`, ...). Its environment holds only `Secrets__Address`, `Secrets__Token`, `Secrets__Path` and the tenant binding (ADR-0016). An explicit environment variable wins over the secret.

The fleet writes `tenants/<slug>/database` (the role passwords), `tenants/<slug>/storage` (the signed container URL), `tenants/<slug>/unit` (the unit's configuration) and `tenants/<slug>/fleet` (the unit token, the smoke client's secret, the invitation link) per tenant (ADR-0018). `make secret-dev` writes the dev tenant's by hand; `make secret-control` writes the control plane's, with `localhost` hosts for the hosts from source and `postgres`/`blob` hosts when `make up-control` writes it for the containers.

OpenBao runs in server mode and keeps its data on the volume `openbao-data` (ADR-0028): `make down`, a restart of its container and a restart of WSL keep every secret. The configuration is `infra/compose/openbao/openbao.hcl`. At every start the static seal unseals the store with `OPENBAO_UNSEAL_KEY` from `.env`, which `make env` generates (and adds to an older `.env`). The first `make up` initialises the store: `infra/compose/scripts/init-store.sh` initialises it with one recovery share, writes `OPENBAO_ROOT_TOKEN` and `OPENBAO_RECOVERY_KEY` into `.env`, and enables KV version 2 at `secret`. Every later `make up` checks that the store is unsealed and that the token opens it, and says what to do if not (section 10). The three lines belong to the volume: keep them together, and never commit `.env`. `make reset` deletes the volume and the token and recovery key lines together; the next `make up` initialises a new, empty store.

If the store lost its data anyway, the recovery is two commands: `make up-control` writes the control plane's secret again, and `make update-unit TENANT=<slug>` heals each unit (section 5).

## 5. Tenant units

`make provision TENANT=acme [ADMIN=admin@acme.localhost]` registers the tenant with the hostname `acme.localhost` and asks the fleet to provision it, then follows the job and prints every step with its duration; the last line is the invitation link. The job's eight steps are reserve, database, storage, secrets, migrate, seed, unit, verify. A failed step is printed with its error; fix the cause and run the same command again, and the job continues from that step. `GET /api/v1/tenants/acme/provisioning` on the control plane shows the job and its steps.

`make update-unit TENANT=acme` runs database, storage, secrets, migrate, seed, unit and verify (ADR-0028): it re-runs migrate and seed from the current images and re-applies the unit, which is how a unit takes a new image (`make images` first) or a changed template, and it heals a unit whose secrets the store lost. With the secrets in place the first three steps change nothing; with them lost, they make new role passwords, a new container signature and a new unit token, and the unit restarts with them. A healed tenant keeps its data and serves its users, and its smoke client takes the new secret from the seed, so a client credentials token works again; its administrator's password cannot be recovered, so run the smoke and the browser tests against a fresh tenant (section 8) until the reset flow of S3. The seed re-issues the administrator's invitation while it is still unaccepted. `make deprovision TENANT=acme` takes the unit's containers down and keeps its database, container and secrets.

What the fleet renders is in `infra/compose/units/<slug>/` (git-ignored): one `compose.yaml` from the embedded template and the unit's `.env`. `docker compose -p tenant-acme ps`, `logs` and `images` work on it directly; `images` is the quick way to confirm a unit runs the image you just built.

## 6. The control plane

From source, for daily work: `make secret-control && make migrate-control && make seed-control` once, then `make run-control` (API on :5200) and `make run-fleet` (the fleet worker with the Compose runtime, driving this machine's Docker) in two terminals. The seed creates the `operator` account (tenant-administrator) and the `smoke-reader` account (service-default, for the smoke's denial case); re-run `make seed-control` after pulling a change that adds accounts.

As containers, as the pipeline runs it: `make up-control` writes the control secret with container hosts and starts `control-api` and `fleet` from the images. The two cannot run beside the source processes: stop `make run-control` and `make run-fleet` first (port 5200), and run `make secret-control` again before going back to source.

## 7. The dev tenant from source

For work on a module without a provisioned tenant: `make migrate`, `make seed` (system roles, the administrator `admin@dev.localhost` with its invitation token in the log, the `smoke-client`), then `make run-api` on :5100 and `make run-worker` on :5101, bound to tenant `dev`. `make secret-dev` and `make run-api-secrets` run the same API the way a unit runs, from its secret. For shell work, `make shell-install` once, then `make shell-dev`: Vite on :5173 proxies the API paths to :5100, so the session cookie is first-party.

## 8. Proving a change

- `make test` and `make shell-check`: the unit and integration tests; the data tests start PostgreSQL, Azurite and OpenBao with Testcontainers, so Docker must be running.
- `make smoke [TENANT=x]`: eleven steps against the live stack with the control plane and the fleet running and the telemetry profile up; provisions the `smoke` tenant (or `TENANT`) once and reuses it; writes `smoke-report.json`; keeps the administrator's password it chose at `secret/tenants/<tenant>/smoke`. It needs a tenant whose administrator it can still sign in as: a fresh one, or one it set up itself. After a heal, `make provision TENANT=<new>` and `make smoke TENANT=<new>`.
- `make e2e [TENANT=x]`: the Playwright tests against `https://<tenant>.localhost` with that administrator, read from `secret/tenants/<tenant>/smoke` (browsers once with `cd shell && npm run e2e:install`; `E2E_BASE_URL` points them elsewhere). Run `make smoke` with the same `TENANT` first.
- `make integration`: the pipeline's stage on this machine: platform and control plane from images, the control database migrated and seeded through the control-plane image, then the smoke (`TENANT=smoke` on a laptop whose `acme` administrator has been accepted by hand).

## 9. Known limits of the laptop stack

- A unit healed after a lost store has new credentials; its administrator's password is gone until the reset flow of S3 (section 5).
- PostgreSQL is the 19 release candidate until the GA image; an RC to GA change can require `make reset`.
- Azurite implements most of the Azure Blob API, not all of it. Reading container properties is refused under a container signature, so the units check their container by listing one page instead.
- Tenant units share the platform network; isolation between tenants is the database roles, the container signatures and the unit tokens (proven by the Fleet tests), not the network. Network policies arrive on Kubernetes.
- The chiseled images print `Cannot load library libgssapi_krb5.so.2` once at start: Npgsql probes for Kerberos, which the image does not carry; the connection uses SCRAM and works.

## 10. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `make up` ends with `tempo did not become ready` | `make logs SERVICE=tempo`; usually a configuration error after a version bump |
| `make up` stops on a service with a health timeout | `make logs SERVICE=<name>`; most often a port is taken on the laptop |
| Tenant units restart again and again; `docker logs tenant-<slug>-api-1` says `The secret store refused to read 'tenants/<slug>/unit': 403` | The store lost its data or was replaced (a store from before ADR-0028, or a `make reset` under running units): `make up-control` writes the control plane's secret, then `make update-unit TENANT=<slug>` for each unit heals it (section 5). For the smoke, provision a fresh tenant |
| `make up` ends with `The store stays sealed` | `OPENBAO_UNSEAL_KEY` in `.env` is not the key the store was initialised with (`make logs SERVICE=openbao` shows `failed to unseal`). Put the right key back; without it the data is locked, and `make reset` starts over with a new store |
| `make up` ends with `The root token in infra/compose/.env does not open the store` or `has no OPENBAO_ROOT_TOKEN` | The token line was lost or belongs to an earlier store. With `OPENBAO_RECOVERY_KEY`, `bao operator generate-root` inside the container makes a new root token; put it in `.env`. Otherwise `make reset` |
| `docker compose` says `OPENBAO_UNSEAL_KEY is missing` | The `.env` predates ADR-0028: `make env` adds the key and changes nothing else |
| `permission denied` on `/var/run/docker.sock` | Your user is not in the `docker` group: `sudo usermod -aG docker $USER`, then reopen the terminal |
| `bind: address already in use` for port 80 or 443 | Another web server on Windows or WSL; stop it or publish Traefik on other ports |
| `failed to bind host port 0.0.0.0:5200` when `make up-control` or `make integration` starts | The control plane from source is still running: `ss -ltnp 'sport = :5200'` shows the process; stop it |
| A control container fails to resolve `openbao` or `postgres` ("temporarily unavailable") | It was created during a failed start and kept a broken network namespace: `docker rm -f <container>`; the Makefile and the integration script recreate the two control containers on every start since T18 |
| `npm` errors with `UNC paths are not supported` or `CMD.EXE was started` | The Windows npm is on the PATH in WSL; install Node 26 with nvm inside WSL, `rm -rf shell/node_modules`, `make shell-install` |
| A unit keeps old behaviour after `make images` | The unit still runs the previous image: `docker compose -p tenant-<slug> images` shows `<none>` for it; `make update-unit TENANT=<slug>` after the build, not before |
| `https://<slug>.localhost` answers `404 page not found` | A router without TLS on the `websecure` entry point (fixed in T15; `traefik/traefik.yaml` must have `http.tls` on it) or no unit for that hostname: `docker compose ls` |
| The invitation link says the invitation does not exist | It was accepted already, or the seed ran again and re-issued it: the current link is `invitation_link` in `secret/tenants/<slug>/fleet` |
| The smoke fails at `administrator` for a tenant whose administrator is Active | The password is not in `secret/tenants/<slug>/smoke` (accepted by hand, or the first smoke run before T18): run the smoke against another tenant (`SMOKE_TENANT`, `make integration TENANT=smoke`) or pass `SMOKE_ADMIN_PASSWORD`; a reset flow is S3 |
| The grid shows "Not found" on reload or on a pasted link | The shell image predates T17; `make images` and `make update-unit` |
| Old credentials after recreating `.env` | The volumes still hold the old values: `make reset`, then `make up` |
| `curl` in WSL cannot reach `acme.localhost` | Add the `/etc/hosts` line from `make hosts`, or use `http://localhost` with `-H 'Host: acme.localhost'` |

## 11. Starting over

`make down-control` (if containers), stop `make run-control` and `make run-fleet`, then `make reset` deletes every platform volume, the store's included, and the store's token and recovery key lines in `.env`. The tenant units are separate projects: `docker compose ls` lists them; `docker compose -p tenant-<slug> down` removes each. Then `make up` (which initialises a new store), `make secret-control && make migrate-control && make seed-control`, and provision again. Delete `infra/compose/.env` only together with `make reset`; the credentials and the seal key must match the volumes.
