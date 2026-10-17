# Iron.Identity

Accounts, credentials, password policy, MFA, SSO (OIDC, SAML), SCIM, sessions, tokens, service accounts, API keys, impersonation. Schema: `identity`.

Slice S1 (T10, T11): principals, password login with Argon2id, server-side sessions, invitation acceptance, the OpenIddict server with client credentials, and `GET /api/v1/users`, the one protected operation of the walking skeleton.

Folders: `Api/` endpoints (thin), `Services/` business logic (plain classes), `Data/` entities, repositories and migrations for this module's schema, `Contracts/` the public interfaces and DTOs other modules may use (its own project, `Iron.Identity.Contracts`).

Only `Contracts/` is public. Another module may reference `Iron.Identity.Contracts` and nothing else from this folder; the build checks enforce it.
