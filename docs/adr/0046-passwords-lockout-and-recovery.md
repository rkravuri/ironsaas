# ADR-0046: Passwords: the policy, lockout without revealing accounts, and three ways to recover

**Status:** Accepted
**Date:** 2026-10-21
**Decided by:** Product owner and architect

## Context

S3 task T2 (`docs/prompts/S3.md`) brings TF-012 (password policy, history and lockout), TF-017 (recovery and change) and TF-019 (account security events). Before it:

- S1 checked a password's length only, at 12, from configuration, on the one path that set a password: the invitation.
- Nothing locked an account. A wrong password was counted on the principal and audited, and that was all.
- A tenant whose administrator lost the password could not recover it. A store healed after a loss (ADR-0028) left the smoke without the administrator's password.
- Nobody could be deactivated, so test runs left people behind on every tenant they ran on.

Email arrives with S4's notifications. Until then, links are handed over by a person, as invitations are (A2).

## Decision

**The policy is the tenant's settings** (ADR-0036), checked in the API on every path that sets a password: accepting an invitation, using a recovery link, and changing it with the current one.

| Setting | Default | Allowed |
|---|---|---|
| `identity.password.minimum_length` | 12 | 8 to 64 |
| `identity.password.common_check` | on | on only |
| `identity.password.history_depth` | 10 | 0 to 24 |
| `identity.password.maximum_age_days` | 0 (off) | 0, or 30 to 365; the shell warns 14 days before |
| `identity.lockout.threshold` | 5 | 3 to 10 |
| `identity.lockout.window_minutes` | 15 | 5 to 60 |
| `identity.lockout.duration_minutes` | 15 | 5 to 120 |

- **No composition rules.** Rules about character classes do not make passwords harder to guess: people meet them with predictable patterns, such as `Password1!`. NIST SP 800-63B advises length and a check against known passwords instead. So a password needs:
  - the minimum length, and at most 1,024 characters;
  - not to be the login identifier;
  - not to be on the common list;
  - not to be one of the person's last N passwords.
- **The common list** is the UK National Cyber Security Centre's 100,000 most used passwords. It is bundled with the Identity module, compared without case, and never fetched at runtime. Its source and licence are in `docs/licences.md`. The check cannot be turned off. It is a setting so that its rule is visible to the tenant.
- **History** keeps the newest 24 Argon2id hashes per person, the most the setting allows. A tenant that raises the depth finds them there. Older hashes are deleted, since keeping a hash longer is a risk with no use.
- **Each rule has its own error code.** The codes are `identity.password.too_short`, `too_long`, `is_login_id`, `common` and `reused`, each with the rule as the field problem's code, so the shell marks the rule that failed.
- **Maximum age is optional**, because some regulated customers' procedures require it. It is off by default, as NIST advises. A right password past its age answers 401 `identity.password.expired` with the code of a recovery link instead of a session, and the shell opens the recovery page. The migration dates every existing password from the upgrade, so turning the age on later does not expire everyone at once.

**Lockout belongs to the login identifier, not the account.**

- A row per identifier counts failures within the window. This holds for identifiers that exist and for those that do not.
- At the threshold the identifier is locked for the duration. While it is locked, even the correct password is refused with `identity.login.locked` and one fixed message.
  - The lock is checked before the account is looked up, with the same hashing work. The answer is the same whether an account exists or not (TF-011).
- A lock never changes the principal's status or ends its sessions. Someone guessing at a login cannot sign its owner out.
- A wrong current password given to change a password counts too, so an open session cannot be used to guess the password.
  - A locked change answers 403, not 401, because the session stays open and a 401 would sign the shell out.
- The lock lifts by itself. An administrator with `identity.users.manage` at the person's home unit can also unlock it. A successful sign-in or a recovery clears the count.
- Rows that hold neither a count nor a lock are deleted when a new identifier is counted, so guessing at many identifiers leaves at most an hour of rows. Limiting many identifiers from one address is T9's rate limits.

**A recovery link** is a one-time code valid for 15 minutes. Only its SHA-256 is stored, on the principal.

- A newer link replaces the older one.
- Using it sets a password under the policy, consumes the code, lifts any lock, and ends every session of the person.
- A password the policy refuses leaves the link open for another try within its 15 minutes.
- Deactivating the person closes it. So does changing the password with the current one.

**There are three ways to issue one.**

Every link is the shell's page with its code in the URL's fragment: `/invitations#<token>`, `/recovery#<code>`. A browser never sends a fragment to a server, so opening a link puts its code in no access log (the ingress's, the shell's), no referrer and no trace. The page posts the code in the request's body: `POST /auth/invitations/accept` and `POST /auth/recovery`. Links issued before this change (`/invitations/<token>`) still open.

1. **An administrator, for a person in scope.**
   - This needs `identity.users.manage` at the person's home unit.
   - The link lets its holder sign in as the person, so the administrator must also hold every permission the person holds, tenant-wide and anywhere. This is ADR-0031's rule 1 for granting a role. Without it, a unit's administrator could take over a tenant administrator homed in that unit (`identity.recovery.escalation`).
   - The link is shown once, with a copy button, as an invitation is.
2. **The operator, for a tenant's administrator.** The details are under the next heading.
3. **The person themself, once email exists** (S4). Until then, the sign-in page's "Forgot your password?" says to ask the administrator.

**The operator's recovery.** The control plane cannot reach a unit's database, so it works as an update does: through the fleet.

1. `POST /api/v1/tenants/{slug}/administrator-recovery` asks for the link.
   - It needs `tenancy.administrators.recover`, a critical permission of the control plane's administrators.
   - It needs a reason.
   - It names the tenant's first administrator unless another is given. Only an active tenant is accepted.
2. The control plane records the request in its audit trail, naming the operator, the administrator and the reason. It creates a job of the new kind `Recover`, which carries all three.
3. The fleet runs the image's `recover-admin` command in the unit, with the request in its environment.
4. The command issues the link only for an active person who holds the tenant administrator role for the whole tenant. It records the operator and the reason in the tenant's own audit trail and in the person's security activity. It prints the code once, as the seed prints the first invitation.
5. The link is handed over as the next rule says, never in the job's output. A recovery job never changes the tenant's status, and a new request is always a new job.

**A link from the operator's path never appears in a fleet job's output, a log line or a trace.** This covers the recovery link and the first administrator's invitation, at provisioning or re-issued by an update.

- The fleet gives the image command a one-time place, `tenants/<slug>/handover/<job id>`, emptied first. It also gives a token that lives 10 minutes and may write the tenant's handover places and nothing else: not even read them back. The unit's own token stays read-only.
- The command writes the link there once, with check-and-set 0. The store deletes it when the link stops working: 15 minutes for a recovery link, the invitation's own lifetime for an invitation. The command's log line says it handed something over, never the link.
- The control plane's provisioning report (`GET /api/v1/tenants/{slug}/provisioning`) gives the link once, to the principal who asked for the job, after the job completed, as `handover` with `no-store`. It then deletes the place. Afterwards the report says it was given; anyone else is told it waits for the operator who asked, and is not given it.
- `make recover-admin`, `make provision` and `make update-unit` print it once. Nothing keeps it.
- The scrubbing rule is the second line. Every log provider (the console's, OpenTelemetry's) and every span pass a rule that replaces a link's code with `[redacted]`: a code is 32 or more base64url characters after `/invitations` or `/recovery`, or after "invitation token" or "recovery code". This holds in a message and in its values, in a span's name and its tags.
- Tests prove that neither a job's output, a step's output or error, nor a line the fleet or the commands logged holds a link. They also prove the link is given once and to nobody else, and that the deposit token cannot read and the unit's token cannot write.
- A host run from source, with no store and so no place, writes the dev administrator's link to the console for the developer, not to a log.

`make recover-admin TENANT=x REASON="..."` wraps the call. The smoke runs this recovery on every run, which also gives a run that did not know the administrator's password a new one.

**Security events** are rows of their own, append-only, one per event:

- **Kinds:** password changed, recovery link issued, recovery link used, password set by recovery, login locked, login unlocked, deactivated, reactivated. MFA and impersonation add theirs later.
- **Who caused each:** the person, an administrator, the operator, or the system. The shell says which in words. The administrator's or the operator's name is kept as it was then.
- Each is written with its audit event in the same transaction.
- A person reads only their own, in My profile under "Security activity", newest first.
- Emailing them is S4's: TF-012's lockout email and TF-019's emails are exceptions until then.

**Deactivation and reactivation** need `identity.users.manage` at the person's home unit.

- A deactivated person cannot sign in. Every session ends at once, their tokens are refused, and an open invitation or recovery link closes.
- Nobody deactivates themselves.
- The last active tenant administrator stays. Identity asks Authorization, through `ITenantAdministrators` in its Contracts, who holds the role tenant-wide, under the same lock as the role's removal.
- The role's removal guard now counts only active administrators too. A deactivated administrator keeps the role but cannot use it.
- Reactivation restores the account. A person who never set a password gets a new invitation instead.

## Options considered

- Composition rules (an upper case letter, a digit, a symbol): not chosen. They push people to predictable patterns, and NIST SP 800-63B advises against them.
- The link in the job's output or the fleet's secret, as the first invitation was until T2's review: not chosen. Everyone who can read jobs reads it, it outlives its use, and it reached the fleet's logs.
- Response wrapping, a single-use token for the link: not chosen. The wrapping token would be a link of its own, and it would travel in the job's output.
- Fetching the common list (a breach service) at runtime: not chosen. It sends something derived from the password out of the unit, and the CSP keeps every external host out.
- Lockout as the principal's Locked status: not chosen. It would reveal which identifiers exist, since unknown ones could not lock. It would also end the owner's sessions on someone else's guesses.
- A recovery link valid for longer, or reusable until it expires: not chosen. TF-017 asks for one use and 15 minutes, and a link handed over by a person must be used at once anyway.
- The operator setting the administrator's password directly: not chosen. The operator would know a tenant user's password. With a link, the administrator chooses the password, and both trails show the request.
- The control plane reaching the unit's database: not chosen. ADR-0018 keeps each unit's credentials out of the control plane, and the fleet already runs commands in units.

## Consequences

- Every password a person sets, on any path, meets the same rules. The shell says them before anyone types.
- A lockout says nothing about whether an account exists, and never signs anyone out.
- A tenant whose administrator is locked out of their password can be recovered without a database session, and both trails say who asked and why.
- Test runs deactivate the people they create, and earlier runs' leftovers with them.
- A link reaches only the operator who asked, once. If that answer is lost on the way, the operator asks again: a new recovery job, or an update that re-issues the invitation. The handover places need a policy for the control plane outside the laptop and CI, where it uses the store's root token today. That comes with S6's operator console.
- To revisit: the self-service link by email (S4), and MFA and step-up (T3), which add security events. When email exists, decide whether an administrator's link should still be shown on screen.
