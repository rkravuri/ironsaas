# Iron.Identity

Schema `identity`. Principals, passwords, sessions and invitations (TF-010, TF-011, TF-018, TF-020, TF-021, TF-029; part 1 in S1, ADR-0014). Part 2 (T11) adds OpenIddict tokens for API clients and `GET /api/v1/users`.

| Folder | Holds |
|---|---|
| `Contracts/` | `IdentityPermissions` (`identity.users.read`, `identity.users.manage`), `IdentityErrors`, `IdentityAuditEvents` |
| `Data/` | `IdentityDbContext`, `Principal` (type, status, Argon2id hash, invitation token hash, login counters), `Session` (token hash, activity, absolute limit, end), the design-time factory, generated `Migrations/` |
| `Services/` | `Argon2PasswordHasher`, `SecureTokens`, `SessionService`, `LoginService`, `InvitationService`, `SessionMiddleware` and `SessionCookie`, `RequestPrincipal`, `IdentitySeeder`, `IdentityOptions` |
| `Api/` | `POST /auth/login`, `POST /auth/logout`, `POST /auth/invitations/{token}/accept`, `GET /api/v1/me` |

Configuration (`Identity` section): `Session:CookieName` (`iron.session`), `Session:IdleMinutes` (60), `Session:AbsoluteHours` (12), `Session:TouchSeconds` (60), `Password:MinimumLength` (12), `Argon2:MemoryKb` (65536), `Argon2:Iterations` (3), `Argon2:Parallelism` (1), `Invitation:ValidHours` (72). `Seed:Administrator:LoginId` and `Seed:Administrator:DisplayName` name the first administrator the `seed` command creates.

Model change: edit the entity, then `make ef-add MODULE=Identity NAME=<WhatChanged>` and commit `Data/Migrations`.
