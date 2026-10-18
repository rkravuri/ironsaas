# Iron.Identity

Schema `identity`. Principals, passwords, sessions, invitations and access tokens (TF-010, TF-011, TF-018, TF-020, TF-021, TF-023, TF-029; ADR-0014, ADR-0015).

| Folder | Holds |
|---|---|
| `Contracts/` | `IdentityPermissions` (`identity.users.read`, `identity.users.manage`), `IdentityErrors`, `IdentityAuditEvents` (login, logout, session, invitation and token events) |
| `Data/` | `IdentityDbContext`, `Principal` (type, status, Argon2id hash, invitation token hash, login counters), `Session` (token hash, activity, absolute limit, end), the design-time factory, generated `Migrations/` |
| `Services/` | `Argon2PasswordHasher`, `SecureTokens`, `SessionService`, `LoginService`, `InvitationService`, `SessionMiddleware` and `SessionCookie`, `BearerTokenMiddleware`, `TokenService` and `SigningKeys` (ES256, RFC 9068), `RequestPrincipal`, `IdentitySeeder` (administrator and smoke client), `IdentityOptions`, `TokenOptions` |
| `Api/` | `POST /auth/login`, `POST /auth/logout`, `POST /auth/invitations/{token}/accept`, `GET /api/v1/me`, `POST /connect/token`, `GET /.well-known/jwks.json`, `GET /api/v1/users` |

Configuration (`Identity` section): `Session:CookieName` (`iron.session`), `Session:IdleMinutes` (60), `Session:AbsoluteHours` (12), `Session:TouchSeconds` (60), `Password:MinimumLength` (12), `Argon2:MemoryKb` (65536), `Argon2:Iterations` (3), `Argon2:Parallelism` (1), `Invitation:ValidHours` (72). `Seed:Administrator:LoginId` and `Seed:Administrator:DisplayName` name the first administrator the `seed` command creates; `Seed:ServiceAccounts` (a list of `ClientId`, `Secret`, `Role`, `DisplayName`) the service accounts: the smoke client in a unit, the operator in the control plane. An account that exists keeps its secret when the seed is given the same one; given another, it takes the new secret and the seed writes `identity.service_account_secret_changed` in the same transaction, which is how a unit healed after a lost store (ADR-0028) gets its smoke client back. `Tokens` section: `AccessTokenMinutes` (15), `Audience` (`iron-api`), `Issuer` (default `https://<primary hostname>`), `SigningKeyPem` or `SigningKeyPath`.

Model change: edit the entity, then `make ef-add MODULE=Identity NAME=<WhatChanged>` and commit `Data/Migrations`.
