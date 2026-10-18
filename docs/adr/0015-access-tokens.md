# ADR-0015: Access tokens for API clients

Date: 2026-10-18
Status: Accepted

## Context

TF-020 and TF-023 in their S1 form: a service account gets a 15-minute access token through the client credentials grant, tokens are signed with a key from the secrets store and validated for issuer, audience, lifetime and signature with zero clock skew, and a token must carry the unit's tenant (TF-003). The S1 plan named OpenIddict as the OAuth 2.1 server. OpenIddict's value is in the authorization code flow, refresh tokens with reuse detection, discovery for partners and SSO, none of which S1 uses, and its Entity Framework stores bring tables that do not follow the data conventions (ADR-0008).

## Decision

1. **S1 issues client-credentials tokens in-house**, with `Microsoft.IdentityModel.JsonWebTokens` 8.23.0 (MIT, the library OpenIddict and ASP.NET Core use underneath) for creation and validation. `POST /connect/token` accepts `grant_type=client_credentials` with HTTP Basic or form client credentials, and answers in the OAuth shape (`access_token`, `token_type`, `expires_in`, `scope`; `invalid_client`, `unsupported_grant_type`) with the correlation id added. `GET /.well-known/jwks.json` publishes the public key.
2. **Clients are principals.** A service account is a `Principal` of type ServiceAccount whose secret is an Argon2id hash in the same column as a password; it holds roles like anyone. The seeder creates the smoke client from `Seed:SmokeClient:*` with the service-default role; the fleet generates the secret per unit (T14). People cannot log in with a service account and service accounts cannot get a session cookie.
3. **Token shape.** RFC 9068 (`typ: at+jwt`), ES256, issuer `https://<primary hostname>` (or `Tokens:Issuer`), audience `iron-api` (or `Tokens:Audience`), 15 minutes (`Tokens:AccessTokenMinutes`), claims `sub` (principal id), `jti`, `client_id`, `tenant`, `principal_type`, `scope`. The same claims are what OpenIddict will issue later, so validation does not change when the issuer does.
4. **Validation on every request.** `BearerTokenMiddleware` runs after the session middleware: the library checks signature, algorithm, type, issuer and audience; the lifetime is checked on Foundation's clock with zero skew; the tenant claim must equal the unit's tenant; the principal behind the token must exist and be Active. A bad token sets `auth.token_invalid`, a stale one `auth.token_expired`, and the permission filter answers 401 with `WWW-Authenticate: Bearer error="invalid_token"`. Cookie and token end as the same `ICurrentPrincipal`, so authorization code is written once (ADR-0012, ADR-0014).
5. **Keys.** One EC P-256 key per unit: from `Tokens:SigningKeyPem`, from the file at `Tokens:SigningKeyPath` (written on first start when missing), or ephemeral with a warning. T12 points the path at the unit's secrets mount; rotation keeps previous public keys for validation (TF-020 AC3) and arrives with it.
6. **OpenIddict joins with the authorization code flow** (S2 or S3, version 7.7.2 at the time of writing). The token endpoint and the client store move to it then; the bearer validation, the claims, the principals and the permission filter stay.

## Consequences

- The smoke client, the pipeline (T18) and partner integrations work now, with standard OAuth client behaviour.
- The in-house endpoint supports one grant and one client authentication style on purpose; anything beyond client credentials waits for OpenIddict rather than growing here.
- Tokens are not revocable before expiry; the per-request principal check means disabling the service account stops them within one request (TF-029 AC2), and 15 minutes bounds the rest.
