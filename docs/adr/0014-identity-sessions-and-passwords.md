# ADR-0014: Principals, passwords and sessions

Date: 2026-10-18
Status: Accepted

## Context

TF-010, TF-011, TF-018, TF-020, TF-021 and TF-029 in their S1 form: one principal model, Argon2id passwords, a fixed failure message, invitations with a time-limited token, a server-side session behind a cookie that holds nothing but a random token, idle and absolute limits, logout that ends the session server-side, and audit events for all of it. The S1 plan named "ASP.NET Core Identity stores"; this record settles what that means.

## Decision

1. **Own tables, not the ASP.NET Core Identity schema.** `identity.principals` and `identity.sessions` follow the data conventions (ADR-0008): UUID v7 keys, audit columns, snake_case, the schema check. `IdentityDbContext` with `UserManager` and `AspNet*` tables would break every one of those, and its features (claims in cookies, external logins, two-factor tokens) are covered elsewhere: OpenIddict issues tokens (T11), the session store replaces the cookie handler, SSO plugs into the login page later. ASP.NET Core's cookie authentication handler is not used either, because it puts claims in the cookie; here the cookie holds a random token and the row is the truth, so revocation is immediate (TF-029).
2. **Principals.** One table for users, service accounts and API key holders: type, status (Invited, Active, Locked, Disabled), lowercase unique login identifier, profile fields, the Argon2id hash, failed-login counters, and the hash of an open invitation token with its expiry. Status drives everything: only an Active principal with a credential can log in.
3. **Argon2id.** `Konscious.Security.Cryptography.Argon2` (MIT, managed). A 16-byte random salt per password, a 32-byte hash, stored as a PHC string with its parameters, so parameters can change (`Identity:Argon2:*`) without re-hashing old passwords. Verification reads the parameters from the stored value and compares in constant time. An unknown login identifier still runs one verification, so the failure takes the same time either way.
4. **Login.** `POST /auth/login` answers every failure with the same code `identity.login.invalid` and the same message, counts the attempt on the principal, and audits `identity.login_failed` with the real reason (unknown login, invited, disabled, wrong password) for administrators. A success writes the login stamp, the session row and `identity.login_succeeded` in one unit of work, with the new session's principal as created_by. Lockout (TF-012) builds on the counters in S3.
5. **Sessions.** The cookie `iron.session` carries a 256-bit random token; the table stores its SHA-256. Secure, HttpOnly, SameSite=Strict, Path=/, no expiry attribute (the browser drops it on close; the server decides the real end). `SessionMiddleware` resolves the cookie on every request: a live session becomes the request principal and has its activity stamped at most once per minute; a session past 60 idle minutes or 12 hours is ended with an audit event and the 401 says `auth.session_expired`; no cookie means nobody and `auth.not_authenticated`. Logout ends the row and clears the cookie; the old cookie then serves nothing.
6. **The request principal.** `RequestPrincipal` replaces the default system principal: the signed-in principal in a request, nobody when the request has no valid session, the system principal outside a request (jobs, commands). Every host lists Identity, so workers keep acting as the system.
7. **Invitations.** A random token in the link, its hash on the principal, 72 hours by default; re-inviting replaces the token. `POST /auth/invitations/{token}/accept` sets the password under the S1 rules (12 characters, not the login identifier), activates the account and audits it. The seeder creates the first administrator in Invited status with the tenant-administrator role and writes the token to its log once, because no email leaves a unit in S1; the fleet prints the link (T14).
8. **Signed-in endpoints.** `RequireSignedIn()` is a third explicit marker for operations any signed-in principal may call (`/api/v1/me`, logout). The start-up check accepts it; the anonymous allow-list of a serving host is health, login and invitation acceptance, and the host test keeps it fixed.

## Consequences

- Every write a request makes after login is stamped with the user; audit events of failed logins carry the principal as actor and nobody as created_by, which is the truth of that request.
- The idle limit is enforced per request with one indexed lookup; the activity stamp costs one write per minute per session, not per request.
- Replacing the cookie handler means no `[Authorize]` and no claims principal; authorization is the permission filter (ADR-0012), which already covers tokens in T11 through the same `ICurrentPrincipal`.
- Argon2id with the default parameters takes tens of milliseconds per verification; tests lower the parameters through configuration.
