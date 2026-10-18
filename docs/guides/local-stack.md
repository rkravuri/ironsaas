# Runbook: the laptop platform

Everything Foundation needs runs on the developer laptop in containers (PRD section 3.3, decision D-14, ADR-0005): the platform services, the control plane, and one Compose project per tenant unit. This runbook is the reference for starting, reaching, changing and resetting all of it. The first hour from a fresh checkout is in the README; this page is what you come back to.

## 1. Prerequisites

- WSL 2 with Ubuntu, Docker (Engine inside WSL, or Docker Desktop with WSL integration), `docker compose version` 2.x.
- .NET SDK 10 and Node.js 26, both inside WSL. `which npm` must not point under `/mnt/c`: the Windows npm runs through `cmd.exe`, which cannot work from a WSL path, and installs Windows binaries into `node_modules`.
- Ports 80, 443, 5432, 8200, 10000, 3200, 4317 and 4318 free; 5100, 5101 and 5200 free when the hosts run from source.
- Memory: about 2.5 GB for the platform with telemetry, about 0.5 GB per tenant unit, about 1 GB for the control plane as containers. The reference laptop has 32 GB.
- Optional: `mkcert` (`make certs`), Playwright browsers (`cd shell && npm run e2e:install`).

## 2. Commands

| Command | What it does |
|---|---|
| `make up` | Creates `infra/compose/.env` on first use, starts every platform container with telemetry, waits until healthy, prints the endpoints |
| `make up-slim` | The platform without the telemetry stack |
| `make up-ci` | Slim plus the collector and Tempo: what the pipeline uses |
| `make down` / `make reset` | Stop and remove the containers (data kept) / also delete every data volume |
| `make ps`, `make logs`, `make logs SERVICE=postgres` | Status and logs |
| `make hosts`, `make certs` | The `/etc/hosts` line for command line tools in WSL; a trusted TLS certificate |
| `make images` | The five images at tag `dev` (`TAG=x` for another) |
| `make secret-dev`, `make run-api`, `make run-api-secrets`, `make run-worker`, `make migrate`, `make seed`, `make token` | The dev tenant from source, against the platform database |
| `make secret-control`, `make migrate-control`, `make seed-control`, `make run-control`, `make run-fleet` | The control plane from source |
| `make up-control`, `make down-control` | The control plane as containers from the images |
| `make provision TENANT=x [ADMIN=a@b]`, `make update-unit TENANT=x`, `make deprovision TENANT=x` | Tenant units through the control plane |
| `make smoke`, `make e2e`, `make integration` | The smoke test, the browser tests, the pipeline's integration stage |

## 3. The map

| Service | Address | Credentials |
|---|---|---|
| Traefik dashboard | http://traefik.localhost | none |
| PostgreSQL 19 | `localhost:5432`, database `postgres` (platform), `control`, one per tenant | `POSTGRES_SUPERUSER` and its password in `infra/compose/.env` |
| Blob storage (Azurite) | `http://localhost:10000/<account>`, one container per tenant | `BLOB_ACCOUNT_NAME`, `BLOB_ACCOUNT_KEY` in `.env` |
| OpenBao | http://localhost:8200 and http://openbao.localhost, mount `secret` | `OPENBAO_ROOT_TOKEN` in `.env` |
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

OpenBao runs in development mode, in memory: after `make down` or a restart of its container every secret is gone. Then `make secret-control`, and provisioned tenants need their secrets again (a re-provision is not possible for an Active tenant in S1; `make reset` and start over, or keep the platform up).

## 5. Tenant units

`make provision TENANT=acme [ADMIN=admin@acme.localhost]` registers the tenant with the hostname `acme.localhost` and asks the fleet to provision it, then follows the job and prints every step with its duration; the last line is the invitation link. The job's eight steps are reserve, database, storage, secrets, migrate, seed, unit, verify. A failed step is printed with its error; fix the cause and run the same command again, and the job continues from that step. `GET /api/v1/tenants/acme/provisioning` on the control plane shows the job and its steps.

`make update-unit TENANT=acme` re-runs migrate and seed from the current images and re-applies the unit: that is how a unit takes a new image (`make images` first) or a changed template. The seed re-issues the administrator's invitation while it is still unaccepted. `make deprovision TENANT=acme` takes the unit's containers down and keeps its database, container and secrets.

What the fleet renders is in `infra/compose/units/<slug>/` (git-ignored): one `compose.yaml` from the embedded template and the unit's `.env`. `docker compose -p tenant-acme ps`, `logs` and `images` work on it directly; `images` is the quick way to confirm a unit runs the image you just built.

## 6. The control plane

From source, for daily work: `make secret-control && make migrate-control && make seed-control` once, then `make run-control` (API on :5200) and `make run-fleet` (the fleet worker with the Compose runtime, driving this machine's Docker) in two terminals. The seed creates the `operator` account (tenant-administrator) and the `smoke-reader` account (service-default, for the smoke's denial case); re-run `make seed-control` after pulling a change that adds accounts.

As containers, as the pipeline runs it: `make up-control` writes the control secret with container hosts and starts `control-api` and `fleet` from the images. The two cannot run beside the source processes: stop `make run-control` and `make run-fleet` first (port 5200), and run `make secret-control` again before going back to source.

## 7. The dev tenant from source

For work on a module without a provisioned tenant: `make migrate`, `make seed` (system roles, the administrator `admin@dev.localhost` with its invitation token in the log, the `smoke-client`), then `make run-api` on :5100 and `make run-worker` on :5101, bound to tenant `dev`. `make secret-dev` and `make run-api-secrets` run the same API the way a unit runs, from its secret. For shell work, `make shell-install` once, then `make shell-dev`: Vite on :5173 proxies the API paths to :5100, so the session cookie is first-party.

## 8. Proving a change

- `make test` and `make shell-check`: the unit and integration tests; the data tests start PostgreSQL, Azurite and OpenBao with Testcontainers, so Docker must be running.
- `make smoke`: eleven steps against the live stack with the control plane and the fleet running and the telemetry profile up; provisions the `smoke` tenant once and reuses it; writes `smoke-report.json`; keeps the administrator's password it chose at `secret/tenants/smoke/smoke`.
- `make e2e`: the Playwright tests against `https://smoke.localhost` with that administrator (browsers once with `cd shell && npm run e2e:install`; `E2E_BASE_URL` points them elsewhere).
- `make integration`: the pipeline's stage on this machine: platform and control plane from images, the control database migrated and seeded through the control-plane image, then the smoke (`TENANT=smoke` on a laptop whose `acme` administrator has been accepted by hand).

## 9. Known limits of the laptop stack

- OpenBao in development mode loses everything when its container stops (section 4).
- PostgreSQL is the 19 release candidate until the GA image; an RC to GA change can require `make reset`.
- Azurite implements most of the Azure Blob API, not all of it. Reading container properties is refused under a container signature, so the units check their container by listing one page instead.
- Tenant units share the platform network; isolation between tenants is the database roles, the container signatures and the unit tokens (proven by the Fleet tests), not the network. Network policies arrive on Kubernetes.
- The chiseled images print `Cannot load library libgssapi_krb5.so.2` once at start: Npgsql probes for Kerberos, which the image does not carry; the connection uses SCRAM and works.

## 10. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `make up` ends with `tempo did not become ready` | `make logs SERVICE=tempo`; usually a configuration error after a version bump |
| `make up` stops on a service with a health timeout | `make logs SERVICE=<name>`; most often a port is taken on the laptop |
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

`make down-control` (if containers), stop `make run-control` and `make run-fleet`, then `make reset` deletes every platform volume. The tenant units are separate projects: `docker compose ls` lists them; `docker compose -p tenant-<slug> down` removes each. Then `make up`, `make secret-control && make migrate-control && make seed-control`, and provision again. Delete `infra/compose/.env` only together with `make reset`; the credentials must match the volumes.
