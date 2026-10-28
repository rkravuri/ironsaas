# Threat model: sign-in and sessions

- **Modules:** identity
- **Endpoints:** `POST /auth/login`, `POST /auth/second-factor`, `POST /auth/second-factor/passkey-options`, `POST /auth/passkey`, `POST /auth/passkey/options`, `POST /auth/enrolment/authenticator-app`, `POST /auth/enrolment/authenticator-app/confirm`, `POST /auth/enrolment/passkey`, `POST /auth/enrolment/passkey/options`, `POST /auth/invitations/accept`, `POST /auth/recovery`, `GET /auth/password-policy`, `GET /auth/sign-in-options`, `POST /auth/sso/discover`, `POST /auth/sso/start`, `POST /auth/sso/callback`, `GET /auth/sso/{id:guid}/sign-out`, `POST /connect/token`, `GET /.well-known/jwks.json`
- **Reviewed:** 2026-10-28 (S3 T10a)

## What it protects

People's credentials: passwords (Argon2id), authenticator secrets (encrypted with the tenant's data key), passkeys and
recovery codes. Sessions (the session cookie) and access tokens (ES256, signed by the key service). Invitation and
recovery links. Company logins' client secrets, which live in the store.

## Who can reach it

Anyone who can reach the tenant's host name: the sign-in's endpoints are anonymous by design and form a fixed list
(TF-030 AC3). A signed-in person reaches their own sessions and factors; an administrator another person's, with a
permission; a service account reaches `/connect/token` with its client credentials.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | Guessing or stuffing passwords. | Lockout per login identifier, known or not (5 failures in 15 minutes); the common-password check; sign-in at most 10 a minute per address and a proof of work after five failures; MFA required for every role with a high or critical permission. | TF-012, TF-072, TF-013 |
| Spoofing | Phishing a password or a code. | Passkeys, bound to the site's origin; a TOTP code works once per step. | TF-013 |
| Spoofing | A forged company login. | The code flow with PKCE, state and nonce; the ID token checked in full; verified emails of the allowed domains only; step-up through the provider is a fresh sign-in. | TF-014, TF-248 |
| Tampering | A changed cookie or token. | Cookies Secure, HttpOnly and SameSite=Strict by policy; tokens signed by the key service and checked for issuer, audience, algorithm and lifetime on the server's clock. | TF-020, TF-070 |
| Repudiation | Denying a sign-in or a factor change. | Every sign-in, failure, factor change and recovery is a security event and an audit event, sealed in the chain. | TF-019, TF-060, TF-061 |
| Information disclosure | Learning which accounts exist. | The same answer and the same lockout for a known and an unknown identifier. | TF-012 |
| Information disclosure | A link or a secret in a log. | Link codes travel in the URL's fragment and the request body; secrets masked in every log line and span; links one-time and short-lived. | TF-017, TF-073 |
| Denial of service | Locking a victim out on purpose. | The lock lasts 15 minutes and an administrator lifts it; it never ends sessions or changes the account. | TF-012 |
| Elevation of privilege | A stolen session used for a sensitive action. | Step-up for every high-risk action; sessions revoked by "valid after" within a minute; the provider's groups grant roles only within the escalation rule. | TF-248, TF-029, TF-014 |

## What remains

- Someone who knows a login identifier can keep it locked, 15 minutes at a time; the lock is per identifier so it never
  reveals whether the account exists.
- The emails about security events (TF-019) and a person's own recovery link by email wait for S4's notifications.
- Device and login risk signals (TF-027) come later; the rate counters live in each API process until S6.
- Alerts on lockouts and failures are T10b's (TF-079).
