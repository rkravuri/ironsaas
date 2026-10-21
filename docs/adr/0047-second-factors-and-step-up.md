# ADR-0047: Second factors, the MFA policy, recovery of a lost device, and step-up re-authentication

**Status:** Proposed
**Date:** 2026-10-22
**Decided by:** Architect, for the design session's review

## Context

S3 task T3 (`docs/prompts/S3.md`) brings TF-013 (multi-factor authentication) and the new TF-248 (step-up re-authentication). Before it:

- A right password signed anyone in. Nothing asked for a second factor, even of a tenant administrator holding critical permissions.
- An open session was enough for every action, however high its risk, for up to twelve hours.
- T2 (ADR-0046) gave lockout, recovery links and the operator's recovery, and promised that MFA and step-up would add their security events.

Email and SMS arrive with S4's notifications, so TF-013's one-time codes by email or SMS cannot be built yet.

## Decision

### The factors

**An authenticator app** follows RFC 6238 over RFC 4226:

- HMAC-SHA1, 30-second steps, six digits: what every authenticator app does by default.
- A code is accepted for the step before, the step of, and the step after the server's clock, for a phone a few seconds off.
- A code is accepted only for a step later than the last one accepted. A code that was used, or an older one, never works again.
- The code is implemented in the Identity module, about sixty lines, and proven against RFC 6238's test vectors. A library would bring more than it saves.
- Enrolment makes a 160-bit secret and shows it as a QR code and as base32 text in groups of four. The person types the code the app shows, and only then is the factor saved.
- A person has one authenticator app at a time.

**Passkeys** are WebAuthn credentials, through Fido2NetLib 4.2.0 (MIT) on the server and the browser's own WebAuthn API in the shell, with a small base64url bridge of our own:

- A person may have several, each with a name they choose.
- User verification is required at creation and at every use, so a passkey is a full second factor.
- Passkeys are discoverable (resident), so a tenant can let people sign in with a passkey alone.
- The relying party is the tenant's hostname the request came to, and the allowed origins are the tenant's hostnames over HTTPS. A passkey made for one tenant never signs in to another.
- No attestation is asked for. The tenant trusts the person's choice of authenticator as it trusts their choice of authenticator app, and the metadata service would be a network dependency the CSP keeps out.
- The signature counter is kept. An assertion whose counter does not grow is refused.

**Recovery codes:**

- Ten come with a person's first factor, shown once, with copy, download and print.
- Each is 80 random bits, written as sixteen base32 characters in groups of four.
- Only the SHA-256 of the person's id and the code is kept. That is enough for a value nobody can guess, and lets a typed code be found by its hash.
- Each works once. A new set replaces the old one whole. When a person's last factor goes, so do their codes.

**Secrets at rest** (TF-073, TF-074):

- An authenticator app's secret is sealed with AES-256-GCM under `Identity:FactorKey`: 32 random bytes in the unit's secret, which the fleet makes once per unit, as it makes the signing key.
- A sealed secret is a version byte, a 96-bit nonce, the tag and the ciphertext. The person's id is the associated data, so a secret copied onto another person's row does not open. The version byte leaves room for a second key at rotation (TF-075).
- An update keeps the key. A new key would leave every authenticator app unreadable.
- The dev and control secrets keep their key in a file beside the signing key, for the same reason.
- No secret, code or recovery code is ever logged. A test captures every log line of an enrolment and a sign-in and finds none.
- A passkey's public key is not a secret and is kept as it is.

### Signing in

- **The password step creates no session for a person who has a factor, or who must have one.** It opens a challenge of five minutes instead.
- The browser holds the challenge by a random token in an HttpOnly, Secure, SameSite=Strict cookie for `/auth` (`iron.sign_in`). Only the token's SHA-256 is stored. Codes and passkey answers travel in request bodies, never in a path.
- **The second step** takes the passkey's answer, the app's code, or a recovery code. It answers the session, or a refusal that never says which part was wrong.
- **A password past its maximum age** (ADR-0046) is looked at after the second step. Otherwise a right password alone would yield a recovery link.
- **A passkey alone** signs a person in where the tenant's setting allows it (`identity.mfa.passkey_sign_in`, off by default). The passkey names its person through its user handle. An unknown passkey and one that does not verify get the same answer.

**Wrong codes** (TF-013 AC2):

- Every wrong second factor counts against the person: a wrong or replayed code, an unknown recovery code, a passkey that does not verify.
- After every third in a row, no code is checked for a cooling period: 60 seconds, doubling with each further three, up to 15 minutes. The answer gives the seconds left (`retry_after_seconds`), and the shell says them.
- The tenth in a row locks the login identifier as ten wrong passwords would (ADR-0046's lockout, for the tenant's lockout duration), and the count starts again.
- A right factor clears the count.
- The same counting applies to the second factor of a re-authentication.

### The policy (TF-013 AC1)

The policy is five tenant settings (ADR-0036). Changing any of them is itself a high-risk action, behind step-up.

| Setting | Default | Allowed |
|---|---|---|
| `identity.mfa.required_for` | `roles` | `nobody`, `roles`, `everyone` |
| `identity.mfa.roles` | `[]`: every role that holds a high-risk or critical permission | role codes |
| `identity.mfa.allowed_factors` | `both` | `both`, `authenticator_app`, `passkeys` |
| `identity.mfa.grace_days` | 0 | 0 to 14 |
| `identity.mfa.passkey_sign_in` | off | on, off |

- A role is held at any scope: the tenant, a unit or a record.
- The allowed factors govern what may be enrolled. A factor enrolled before a change keeps working until its owner removes it.
- **A person who must have a factor and has none** is sent to enrolment by the password step, through the same five-minute challenge: no session, so no page opens before it.
  - The first factor's recovery codes are shown once.
  - The shell holds its redirect until the person ticks that they saved them, since a read of `/me` on window focus would otherwise take them away.
- **The grace** a tenant may give counts from the first sign-in that found the factor required. Until it ends, the password signs the person in, and `/me` tells the shell the date for its banner.
- **Open sessions** are not ended by a change of policy. They end within their twelve hours, and the next sign-in follows the policy.
- **Service accounts keep their client credentials.** MFA does not apply to tokens.

### A lost device (TF-013 AC3)

- **A recovery code** signs the person in once. The answer asks the shell to open My profile with "Add a second factor".
- **An administrator may reset a person's factors**: every factor and recovery code goes, and every session of the person ends. Their password then signs them in, and enrolment follows at once where the policy requires it. The reset needs:
  - `identity.users.manage` at the person's home unit;
  - ADR-0046's escalation rule: the administrator holds every permission the person holds, since a reset lets the person's password alone in;
  - a reason, kept in the person's security activity;
  - a recent sign-in (step-up).
  - The user record shows "Reset factors" only where `/me/access` and the escalation rule allow it. The record answers the second as `takeover_allowed`.
- **The operator** resets a tenant administrator's factors with `make recover-admin RESET_FACTORS=1`.
  - The flag travels with the recovery job (`reset_factors` in the request and in the job's row) to the unit's `recover-admin` command.
  - The command issues the recovery link, removes the factors, and ends the sessions, in one transaction.
  - Both planes' audit trails show the operator, the reason and the reset.
- **Security events:** every enrolment, removal, reset, recovery-code use and new set of codes is a security event of the person's and an audit event, in the same transaction as the change: `factor_enrolled`, `factor_removed`, `factors_reset`, `recovery_code_used`, `recovery_codes_regenerated`.

### Step-up re-authentication (TF-248)

- **One mechanism.** `IRecentAuthentication` in the kernel answers whether the principal acting now authenticated within a given age.
  - A session's authentication is its sign-in, or its latest re-authentication (`authenticated_at` on the session).
  - A token's is its issue: a client gets a fresh token with its credentials.
  - Outside a request, in a job, a seed or a command, nobody signs in, and the action goes on.
  - The kernel's default, where Identity is not in the host, does the same.
- **Two ways to ask.**
  - An endpoint declares `RequireRecentAuthentication(minutes)`, five by default, after its permission, so a caller without the permission hears that first.
  - A service asks directly where the rule depends on the data:
    - granting a role that holds a high-risk or critical permission, asked last, after every other check;
    - a setting declared `RequiringRecentAuthentication()`, which the MFA policy's five are.
- **The refusal** is 403 `auth.reauthentication_required`, never 401, so the shell does not sign out.
- **The shell's `request()`** hands the refusal to the one re-authentication dialog the layout mounts.
  - The person gives their password and, when they have a factor, their second factor.
  - The request is then sent again, once, so the action goes on without the person repeating it.
  - Requests refused at the same moment share the dialog. Cancelling lets the refusal through.
- **The confirmation** (`POST /api/v1/me/reauthenticate`) answers 403 with what was wrong.
  - A wrong password counts against the login as a failed sign-in does.
  - A wrong code counts as at sign-in.
  - Passkey options come with `POST /api/v1/me/reauthentication`, held by a challenge of the session.
- **The first uses:**
  - issuing a recovery link;
  - resetting someone's factors;
  - deactivating a person;
  - granting a role with a high-risk or critical permission;
  - changing the MFA policy;
  - adding, renaming or removing one's own factors, and making new recovery codes.
  - T5's break-glass, T7's electronic signatures and T8's second approvals use the same mechanism.

### The test tenants

- **The smoke**, on every run:
  - the operator's recovery resets the administrator's factors too;
  - the password step sends the administrator to enrolment;
  - the smoke enrols an authenticator app with a computed code and keeps its key in the store beside the password;
  - a person of the run enrols an app, signs in with its codes and once with a recovery code, and is deactivated.
- **The browser tests' data script** names the roles the tenant requires a factor of: the tenant administrator and `e2e-mfa`. The other people of a run then sign in with a password, as the tests of access expect.
- **The browser tests** sign the administrator in with a code once per worker, then add a passkey in Chromium's virtual authenticator and use it, since a code works once per 30-second step. A test sends the `e2e-mfa` person to enrolment, creates a passkey, and signs in with it.

## Options considered

- **A TOTP library** (Otp.NET): not chosen. The algorithm is short and stable, and RFC 6238's vectors prove it. A dependency would add a supply-chain surface for little.
- **A restricted session for forced enrolment**, with every endpoint but enrolment refusing it: not chosen. A session made by the password alone is what the requirement forbids, and every new endpoint would have to remember the restriction. The five-minute challenge already exists for the second step.
- **The challenge's token in the request body**, from the login's answer: not chosen. An HttpOnly cookie for `/auth` keeps it out of the page's script and of every path.
- **Recovery codes hashed with Argon2id**, as passwords are: not chosen. Eighty random bits cannot be guessed, a salted fast hash is enough, and it lets a typed code be found by its hash.
- **Attestation and the FIDO metadata service** for passkeys: not chosen. The metadata service is an external host the CSP keeps out. Rules about which authenticator models a tenant accepts can come with a regulated customer who asks.
- **Step-up as a 401**: not chosen. The shell signs out on 401, and the session is still good.
- **Step-up only as an endpoint filter**: not chosen. A role grant is high-risk only when the role holds a high-risk or critical permission, and the settings endpoint serves every setting. So the services ask the same interface where the data decides.
- **One-time codes by email or SMS as a fallback** (TF-013's requirement): not built. Notifications are S4's. It is an exception with its home in S4, listed when S3 closes.
- **Ending open sessions when the policy changes**: not chosen. A policy change would sign out every administrator at once. Sessions end within twelve hours, and the next sign-in follows the policy.

## Consequences

- A stolen password is no longer enough to sign in as anyone who holds a high-risk or critical permission, and an open session is no longer enough for the actions that change who can do what.
- A tenant whose administrator lost both their password and their device is recovered by the operator, with the same trail as ADR-0046's recovery.
- **The factor key is a unit secret, kept across updates.** A store that loses the unit's secret loses the key with it. The unit's authenticator apps then cannot be checked and their people need a reset, while passkeys keep working. Rotation, with the version byte, is TF-075's (S3, later).
- The smoke resets and enrols the administrator's factors on every run. The browser tests' tenant names its own MFA roles, so the policy there is not the default. The .NET tests prove the default.
- To revisit:
  - TF-019's emails for a new factor and a reset (S4), and the one-time codes by email or SMS (S4);
  - whether a tenant may require passkeys only for administrators;
  - a security key's attestation where a regulated customer asks.
