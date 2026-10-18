# Slice S1: walking skeleton

Status: in progress. Plan agreed on 2026-10-17 with the product owner. This file is the brief for every build session of S1. Update the task table as tasks land.

S1 delivers the S1 row of the Technical Foundation PRD, section 7: TF-001, 002, 003, 010, 011, 020, 021, 030, 031, 060, 070, 080, 100, 101, 130, 160, 170, 171, 180, 190, 209, 210, 220. Every part is built at its thinnest honest version; the coverage table says where S1 stops and which slice continues.

## 1. What S1 proves

The slice is done when this demo runs on a fresh WSL laptop:

1. `make up` starts the platform containers. Grafana opens at grafana.localhost.
2. `make provision TENANT=acme` creates the tenant in the control plane. The fleet worker creates the database and roles, runs migrations, seeds the first administrator, creates the bucket and the secrets path, starts the tenant containers and waits for health. Each step is logged with its duration. The administrator invitation link is printed.
3. The link opens the shell at acme.localhost. The administrator sets a password and logs in.
4. The Users page shows the administrator in the grid with server paging and sorting. The response carries a correlation id. The same id finds the trace in Grafana, with API and database spans.
5. A second user without the permission gets a 403 with a stable error code. An audit row records the denial.
6. A token issued for tenant bravo is refused by acme's unit. Stopping the blob storage during provisioning leaves the tenant in Provisioning with the failed step named; running provision again resumes from that step.
7. A push to main builds and scans the code, publishes images, starts the stack on the runner, provisions a tenant and runs the smoke test.

## 2. Requirement coverage

| ID | Requirement | What S1 builds | Continues in |
|---|---|---|---|
| TF-001 | Tenant registry | All fields, unique hostname rule, status values; the unit does not yet enforce Suspended | S6 |
| TF-002 | Automated provisioning | All steps, resumable, re-run refused when Active; invitation shown as a link, no email yet | S4 (email) |
| TF-003 | Tenant resolution | Fixed tenant per unit from configuration, hostname check, token tenant claim check, one connection string | done |
| TF-010 | Principals | User, ServiceAccount and ApiKey types with status; invitation acceptance; no profile screens | S3 |
| TF-011 | Password login | Argon2id, fixed failure message, failed attempts counted | S3 (lockout) |
| TF-020 | Sessions and tokens | Server-side session with secure cookie; 15-minute access tokens for API clients; signing keys from the secret store | S3 (refresh rotation, key rotation) |
| TF-021 | Inactivity and limits | 60 minutes idle and 12 hours absolute from configuration; shell returns to login and warns before expiry | S2 (tenant setting), S3 (concurrent limit) |
| TF-030 | Permission catalogue | Permissions declared in code, catalogue at start-up, start-up check, allow-list test | S2 (role editor) |
| TF-031 | Enforcement | Check before business code, 403 with stable code, 60-second cache, fail closed, denial audited | S6 (alert) |
| TF-060 | Append-only audit | Table, writer in the same transaction, database grants block update and delete; events for login, denial, invitation, provisioning | each slice adds its events |
| TF-070 | Secure web defaults | Security headers, cookie flags, CORS from configuration | S3 (CSP reports) |
| TF-080 | Database conventions | Full conventions and the pipeline schema check | done |
| TF-100 | REST conventions | Envelope, problem details with error code and correlation id, snake_case JSON, OpenAPI from code | done |
| TF-101 | Paging, filter, sort | Offset paging capped at 200, multi-field sort, filter grammar, sparse fields, whitelist per resource | done |
| TF-130 | Tracing | One trace per request with API and database spans; correlation id returned and shown on errors; loss counter | S4 (job links) |
| TF-160 | File storage | Storage interface (Azure Blob, against Azurite locally), one container per tenant with its own credentials, readiness probe | S4 (upload, versions, links) |
| TF-170 | Application shell | Login, navigation from module manifests filtered by permission, error page with correlation id, session expiry, route registration with guard | S2 (entitlement and flag filters) |
| TF-171 | Design system | Grid wrapper over AG Grid Community with server paging, sorting, filtering, column chooser, lint rule | S2 to S5 (forms, saved views, panels) |
| TF-180 | Operator console | Control plane API and CLI for create, provision and list | S6 (screens) |
| TF-190 | Repository and conventions | Layout, build checks, one-command stack, one-command provisioning | S2 (scaffolding tool) |
| TF-209 | Docs and ADRs | One ADR per S1 decision, developer guide v1, local stack runbook, API docs from code | ongoing |
| TF-210 | Deployment units | One Compose project per tenant with own network, limits by size class, own roles, blob container, secrets path and routes; de-provision keeps data | S6 (network policies on Kubernetes, retention) |
| TF-220 | Owning organisation | Base record type with owning organisation and sharing mode, plus the build check | S2 (visibility filters) |

## 3. Design

### 3.1 Runtime on the laptop

| Plane | Containers | Hostnames |
|---|---|---|
| Platform services | traefik, postgres 19, blob (Azurite), openbao (dev mode), otel-collector, tempo, loki, prometheus, grafana | traefik.localhost, grafana.localhost, prometheus.localhost, openbao.localhost |
| Control plane | controlplane-api (Tenancy, Fleet, Identity, Authorization, Audit modules), fleet-worker (runs provisioning jobs, has the Docker socket) | control.localhost |
| Tenant unit acme | acme-api, acme-worker in Compose project tenant-acme: own network, own database roles, own blob container, own secrets path, limits by size class | acme.localhost for /api, /auth, /connect, /.well-known, /health |
| Shell | one container serving every tenant hostname | acme.localhost, all other paths |

Traefik routes by hostname and path using container labels. Browsers resolve `*.localhost` on their own; command line tools need /etc/hosts entries. TLS is optional on the laptop (`make certs` with mkcert); plain HTTP works everywhere. Profiles: none for the core, `telemetry` for the telemetry stack, `ci` for the collector and Tempo only, so the pipeline can check traces (ADR-0005).

### 3.2 Module pattern

Each module is one folder with Api, Services, Data and a Contracts project (ADR-0001). Other modules reference only the Contracts project, so the compiler blocks access to another module's Data folder. Each module has one class that implements `IModule`: it registers services, maps endpoints, lists its permissions and names its DbContext. Hosts list their modules explicitly in Program.cs. No reflection scanning. Permissions look like this:

```csharp
public static class IdentityPermissions
{
    public static readonly Permission UsersRead =
        new("identity.users.read", "List and view users", Risk.Low);
}

app.MapGet("/api/v1/users", ListUsers)
   .RequirePermission(IdentityPermissions.UsersRead);
```

### 3.3 Tenant binding (TF-003)

The tenant identifier is a slug such as `acme` (ADR-0007); it is the same in every environment and names the tenant's database, containers, secrets path and default hostname. A unit reads its tenant id and hostnames once at start. A middleware refuses any request whose Host header is not in that list. Bearer tokens must carry a tenant claim equal to the unit's id. The data layer holds one connection string, read from the unit's secrets path. Every span and log line carries the tenant id. There is no tenant switch anywhere in the code.

### 3.4 Login, sessions and tokens

The shell logs in with a JSON call to `POST /auth/login`. The server checks the Argon2id hash, creates a row in `identity.sessions` and returns a cookie that holds only the session id (Secure, HttpOnly, SameSite=Strict). The session ends after 60 minutes idle or 12 hours in total; the values come from configuration until Settings arrive in S2. `GET /api/v1/me` returns the principal, its permissions and the session expiry; the shell uses it for navigation and the expiry warning.

OpenIddict runs in the same host as the OAuth 2.1 server. In S1 it issues 15-minute access tokens through the client credentials flow to a seeded test client, which is a ServiceAccount principal. Signing and encryption keys come from the tenant's secrets path. Cookie and bearer token both end as the same principal type, so authorization code is written once. Later, the same login page serves the OpenIddict authorize endpoint for the mobile app and partner apps, and external providers plug into it for SSO.

### 3.5 Permissions and audit

Permissions are declared in code as `module.resource.action` with description and risk level. Every endpoint carries `RequirePermission` or an explicit anonymous marker. At start-up the host walks all endpoints and fails with the first offender named. The anonymous list is login, invitation acceptance, health, and the OpenIddict discovery and token endpoints; a test keeps the list fixed. Evaluation reads the principal's roles from the `authz` tables with a 60-second cache; any failure denies. Denial returns 403 with error code `authz.permission_denied` and writes an audit event.

Audit events are rows in `audit.events`, written on the same connection and transaction as the change. The runtime database role has INSERT and SELECT only on that table, so no code path can update or delete them. Each tenant gets two roles: an owner role for migrations and seeds, and an app role for runtime.

### 3.6 Data and API conventions

One PostgreSQL schema per module. One DbContext per module with its own migrations history table inside its schema. Tables use snake_case, a UUIDv7 primary key, created and updated by and at, a version column for optimistic concurrency, and is_deleted where deletion is allowed. No foreign keys across schemas. Business records inherit `BusinessRecord`, which adds owning_organisation_id and sharing_mode.

Endpoints are minimal APIs under `/api/v1` with plural nouns and snake_case JSON. Lists return `{data, meta, links}`. Errors return problem details with type, title, status, detail, error_code, correlation_id and field errors. Lists accept page, page_size (max 200), sort, filter and fields through one ListQuery helper with a whitelist per resource. OpenAPI is generated from code and browsed with Scalar.

### 3.7 Telemetry

The OpenTelemetry SDK with ASP.NET Core, HttpClient and Npgsql instrumentation exports over OTLP to the collector, which feeds Tempo, Prometheus and Loki. Logs go through Microsoft.Extensions.Logging and the OpenTelemetry logging provider with the trace id attached (ADR-0010). The correlation id is the W3C trace id. It returns in the `X-Correlation-Id` header, in problem details and on the shell's error page. If the exporter is down, requests still complete and a counter records dropped spans.

### 3.8 Secrets and storage

`ISecretStore` has an OpenBao implementation now (through VaultSharp) and Key Vault later. `IObjectStorage` has one Azure Blob implementation, run against Azurite on the laptop and Azure Blob Storage later (ADR-0006, replaces MinIO). The unit's environment contains only the store address, a unit token scoped by policy to `secret/tenants/<id>`, and the telemetry endpoint. Everything else is read from the store at start. Readiness checks the database, the bucket and the store.

### 3.9 Provisioning steps (fleet worker)

Each step is recorded with status and duration. A re-run continues from the first step that did not complete. A re-run on an Active tenant is refused.

1. Reserve: validate the definition, check hostname uniqueness, set status Provisioning.
2. Database: create the owner and app roles and the database.
3. Migrate: run the API image with the `migrate` command and the owner connection string.
4. Seed: run the API image with the `seed` command: reference data, the tenant-administrator role with all permissions, the first administrator in Invited status with an invitation token, grants for the app role.
5. Storage: create the tenant's blob container and a container-scoped access signature.
6. Secrets: write the app connection string, storage credentials and generated token keys to the tenant path; create the policy and one unit token.
7. Unit: render the Compose file from the template (image tag, hostnames, size class limits, unit token) and apply it with `docker compose`.
8. Verify: wait for readiness through the ingress, set status Active, write the audit event, print the invitation link.

De-provision in S1 removes containers and routes and sets Decommissioned. Data stays (retention hold); the deletion job is S6.

### 3.10 Shell and grid

One React build serves every tenant hostname. Modules register a manifest: name, routes, required permissions, navigation entries. The shell filters navigation by the permissions from `/me`; entitlements and flags join in S2. A route without a permission throws in development and is hidden in production. The error boundary shows the correlation id. `DataGrid` wraps AG Grid Community, maps its paging, sorting and filter state to the ListQuery grammar and reads the standard envelope. An eslint rule blocks ag-grid imports outside the wrapper, so Enterprise can be switched on by licence in one place.

### 3.11 Build checks in S1

- Module boundary: Contracts projects only (compiler), and the architecture test names any violation. (T1, done)
- Standard columns and snake_case on every table; no cross-schema foreign keys (schema check against a real database). (T4, done; runs in the pipeline against migrated databases from T7)
- Owning organisation on every business entity of a product (model check in the architecture tests). (T4, done)
- Permission or anonymous marker on every endpoint (start-up check and test); fixed anonymous list. (T8)
- No raw ag-grid import outside the wrapper (eslint). (T16)
- No direct reads of the system clock; time comes from TimeProvider (T3, done).
- Warnings as errors, nullable enabled (T1, done); gitleaks on every push (T18).

### 3.12 Pipeline

`ci.yml` on pull requests: restore and build, architecture tests, unit tests, integration tests with Testcontainers PostgreSQL, schema check, shell lint, type check, tests and build, gitleaks, Trivy. `release.yml` on main: build the five images (api, worker, controlplane, fleet, shell), tag with the commit SHA, push to GitHub Container Registry, then an integration job starts the stack from those images on the runner, provisions tenant acme, runs the smoke, uploads the report and tags the images `integration`. There is no Azure yet, so integration is a short-lived stack on the runner. A long-lived integration machine can later be a self-hosted runner that runs the same job.

### 3.13 Scale patterns kept from day one

Stateless API nodes; sessions in the database so any node serves any request; UUIDv7 keys; no cross-module foreign keys; owning organisation on every business record for later partitioning; Npgsql pooling now and PgBouncer later; one image for all tenants with limits by size class.

### 3.14 Deliberately not in S1

MFA, SSO, lockout, rate limits, role editor, entitlements, flags, settings, email, jobs engine, outbox, file upload and download, forms library, saved views, operator console screens, Kubernetes, Azure, scaffolding tool. They arrive in S2 to S6 as planned.

## 4. Task breakdown

| # | Task | We build | Done when | State |
|---|---|---|---|---|
| T1 | Repository skeleton and build checks | Solution, Directory.Build.props and Directory.Packages.props (versions with licences), .editorconfig, Makefile, README, ADR template and first ADRs, architecture test project with the module boundary rule, CI workflow for build and tests, this plan in docs/slices | `dotnet build` passes with warnings as errors; a test reference from one module to another module's Data fails the architecture test naming the file; CI is green | done |
| T2 | Platform stack with Compose | traefik, postgres 19, Azurite, openbao, otel-collector, tempo, loki, prometheus, grafana; profiles telemetry and ci; hostnames, optional TLS; Grafana data sources and one request dashboard; make up, up-slim, up-ci, down, reset, logs; runbook | `make up` brings every platform container to healthy; Grafana shows the three data sources; `make up-slim` starts without telemetry | done |
| T3 | Iron.Core | Result and Error types, TenantId and TenantContext, Ids (UUID v7), CorrelationId, paging and ListQuery model, Money and Currency, unit tests, build check against direct clock reads | Tests pass; the project has no third-party dependencies | done |
| T4 | Iron.Data | Base entities (Entity, Audited, AppendOnly, BusinessRecord, ISoftDeletable), snake_case and schema conventions, version column, audit stamp interceptor, module DbContext base with its own migrations history, ICurrentPrincipal, schema check (live database) and model check, Testcontainers tests, architecture test over every module context | The schema check fails on a table without standard columns and on a cross-schema foreign key; the owning-organisation check fails on a test entity without it | done |
| T5 | Iron.Api | IModule with AddModules and MapModules, MapApiGroup, snake_case JSON, list envelope with links, problem details with error codes and correlation id, unexpected exception handler, FluentValidation filter with field errors, ListQuery grammar and ListFields whitelist (parser in Api, applier in Data), sparse fields, OpenAPI and Scalar (Development only), in-memory server tests | A sample endpoint returns the envelope; page size 500 is capped to 200 with a note in meta; a non-whitelisted filter field returns a problem naming the field | done |
| T6 | Iron.Telemetry and Iron.Security (thin) | OpenTelemetry traces, metrics and logs over OTLP, ASP.NET Core, HttpClient and Npgsql instrumentation, service and tenant on every signal, correlation middleware with X-Correlation-Id, health endpoints with a PostgreSQL readiness check, security headers, cookie policy, CORS from configuration, hosts wired, make run-api | A request shows one trace in Tempo with API and database spans; every response carries the TF-070 headers and X-Correlation-Id | done, to verify on the laptop |
| T7 | Hosts: Iron.Host.Api and Iron.Host.Worker | IModule pattern, explicit composition, tenant binding, hostname check middleware, migrate and seed commands, Dockerfiles, worker heartbeat trace | The API image starts bound to one tenant; a request with another hostname is refused; migrate applies all module migrations; the heartbeat trace carries the tenant id | done |
| T8 | Iron.Authorization (thin) | Permission type and catalogue, RequirePermission metadata, start-up check and allow-list test, roles and assignments tables, seeded tenant-administrator role, evaluation with 60-second cache, fail closed, 403 problem, denial audit | Start-up fails naming an endpoint without a permission; a principal without the permission gets 403 and an audit row | done (the audit row lands with the table in T9; T8 writes the event through the audit contract) |
| T9 | Iron.Audit (thin) | audit.events table, IAuditWriter on the same transaction, grants for the app role, S1 event types | UPDATE and DELETE on audit.events as the app role are refused by PostgreSQL; login and denial write rows | done (denials write rows; login rows come with T10) |
| T10 | Iron.Identity part 1: principals and login | Principal model, ASP.NET Core Identity stores, Argon2id hasher, server-side session store with idle and absolute limits, cookie settings, login, logout, me, invitation acceptance, audit events | Correct login creates a session row and the cookie; wrong login returns the fixed message and counts the attempt; the stored hash is Argon2id; an expired session returns the session-expired problem | done |
| T11 | Iron.Identity part 2: OpenIddict and the protected operation | OpenIddict server with token endpoint and client credentials, 15-minute tokens, keys from the secret store, bearer validation with tenant claim check, seeded smoke client, GET /api/v1/users with ListQuery whitelist | The smoke client gets a token and lists users; a token for another tenant is refused; an expired token is refused; OpenAPI shows the permission | done (in-house client credentials; OpenIddict deferred to the authorization code flow, ADR-0015) |
| T12 | Iron.Files and secrets (thin) | ISecretStore with OpenBao, IObjectStorage with Azure Blob (Azurite locally), start-up reads connection string, storage credentials and keys from the secrets path, readiness checks | The API starts with only the store address and unit token in its environment; readiness fails with a clear message when the container is missing | done |
| T13 | Iron.Tenancy and Iron.Host.ControlPlane | Tenant registry, control plane API (create, list, get, request provisioning), hostname uniqueness, operator service account with client credentials, Dockerfile | A duplicate hostname is refused; a provisioning request creates the fleet job; make provision authenticates and calls the API | done |
| T14 | Iron.Fleet and Iron.Host.Fleet | Provisioning job and step records, IUnitRuntime with the Compose implementation, the eight provisioning steps, resume from the failed step, refusal on Active, size classes, thin de-provision, fleet worker image with the docker CLI | `make provision TENANT=acme` serves acme.localhost within minutes and prints the invitation link; a failed step is reported and resumed; bravo runs beside acme and each unit reaches only its own database | in progress: T14a done (runner, step records, resume, database, storage and secrets steps, isolation proven on containers); T14b next (Compose runtime, image commands, readiness, worker image) |
| T15 | Shell foundation | Vite, React 19.3, TypeScript, Mantine 9.7.3, TanStack Query, react-i18next (English), Zod on Node v26.12.0; AppShell, navigation from manifests filtered by permissions, route registration with permission guard, login page, session handling, error boundary with correlation id, API client, Dockerfile and route | Login works on acme.localhost; navigation shows Identity and Users; a route without a permission fails in development | |
| T16 | DataGrid and the Users page | AG Grid Community wrapper with server paging, sorting and filtering mapped to the ListQuery grammar, column chooser, page size cap notice, keyboard navigation, loading and empty states, theming tokens; eslint rule; Users page | The grid lists users with server paging and sorting; a bad filter shows the problem message; a raw ag-grid import in a page fails lint | |
| T17 | Smoke and end-to-end tests | Iron.Smoke: provision or reuse, login, protected call with cookie and with token, denial case, trace lookup in Tempo by correlation id; Playwright: login and Users grid | `make smoke` passes against the local stack | |
| T18 | Pipeline | ci.yml with build, checks, tests, shell checks, gitleaks and Trivy; release.yml with images to GHCR, integration stage on the runner with provisioning and smoke, image tagging | A pull request runs CI; a merge produces images and a green integration run with the smoke report | |
| T19 | Documentation and definition of done | Developer guide (add a module, an entity, an endpoint with permission), local stack runbook, ADRs complete, README quick start, definition of done checklist | A fresh checkout on a 32 GB WSL machine reaches a provisioned tenant and a visible trace in under one hour by following the README | |

T10, T11 and T14 may each split into two sessions.

## 5. Decisions confirmed on 2026-10-17

1. Login model: the shell uses a cookie session (ASP.NET Core Identity with a server-side session store); OpenIddict issues tokens for API clients and later serves the mobile app, partners and SSO through the same login page.
2. Contracts is a separate project per module (ADR-0001).
3. Laptop fleet runtime: the fleet worker renders a Compose file per tenant from a template and applies it with `docker compose`; on Azure the same pattern renders a Kustomize overlay (ADR-0002).
4. Integration environment in S1 is a short-lived stack on the GitHub runner.
5. The one protected operation is `GET /api/v1/users` with permission `identity.users.read`.
6. TF-220 in S1 is the base record type plus the build check only.
7. Versions: .NET 10 LTS, PostgreSQL 19, Node.js v26.12.0, React 19.3, Mantine 9.7.3, AG Grid Community current release (ADR-0003).
8. Control plane access in S1: the control plane composes Identity, Authorization and Audit like a tenant unit; `make provision` gets a client credentials token for a seeded local operator service account.

9. Azurite replaces MinIO as the laptop object storage, because MinIO's community edition ended (ADR-0006, confirmed 2026-10-18). PostgreSQL 19 runs as release candidate 1 until the GA image, planned for 2026-11-19.
10. Logging stays on Microsoft.Extensions.Logging with the OpenTelemetry provider instead of Serilog (ADR-0010).

## 6. How we build

Each task is one session. The files and commands are produced, run in WSL, and the task is done when its "Done when" line holds and its ADR is written. Update the State column of the task table when a task lands.
