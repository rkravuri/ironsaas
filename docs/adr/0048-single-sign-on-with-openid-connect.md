# ADR-0048: Single sign-on with OpenID Connect: providers, the code flow, who it signs in, and a test provider

**Status:** Accepted
**Date:** 2026-10-22
**Decided by:** Product owner and architect, at the design session's review of S3 T4 (2026-10-22)

## Context

S3 task T4 (`docs/prompts/S3.md`) brings TF-014: a tenant connects its own OpenID Connect providers (Microsoft Entra ID, Google Workspace, Okta or another), marks people as signing in only through them, and lets a new employee of an allowed domain be created at their first sign-in. Before it:

- People signed in with a password and, since T3, a second factor (ADR-0047). Nothing knew of an outside identity provider.
- The unit's store token could only read (ADR-0046): a unit had nowhere to keep a secret it was given.
- The laptop and the pipeline had no identity provider to sign in through.

SAML (TF-015) and SCIM (TF-016) stay in S7.

## Decision

### The test provider, chosen by measurement

**navikt's mock-oauth2-server 6.0.4** (MIT, Kotlin, maintained, 2026-09-29) is the test provider. Measured on 2026-10-22 on the reference machine, each running twenty authorization code flows with token exchange:

| | Memory idle | After twenty flows | Claims a test sets per sign-in |
|---|---|---|---|
| mock-oauth2-server 6.0.4 | 142 MiB | 166 MiB | any JSON on its login page: email, `email_verified`, name, groups, `amr` |
| Dex v2.46.0 (Apache-2.0) | 8.4 MiB | 11.2 MiB | fixed per static user: email, `email_verified` always true, name; no groups, no `amr` |

- Dex is far lighter, but a test cannot make it send an unverified email, groups or `amr`, which the tests must prove. The mock does, and it verifies PKCE: a wrong verifier is refused.
- In the stack it is service `idp` of Compose profile `sso`, capped at 300 MB (the budget), behind the ingress at `http://idp.localhost`. Measured there on 2026-10-22 after a smoke run and two browser test runs signed in through it: 150 MiB, no restart. Any first path segment is an issuer of its own, so each tenant and each run names its own.
- `make smoke` and `make e2e` start it (`make test-provider`), and the integration stage before the smoke; `make up` does not.
- **One address for everyone.** Traefik carries `idp.localhost` as a network alias, so a unit's API reaches the provider at the address the browser and the smoke use, and the provider, which derives its issuer from the request's host, gives one issuer to all three. .NET resolves the name through the operating system (checked in a container); curl does not, and resolves `*.localhost` itself.
- The .NET tests run it as the run's fourth shared service (`tests/test-services.sh`), or a container of their own when a project runs alone (`TestOidcProvider`).

### Providers, per tenant

A provider is a row of its tenant (`kernel_identity.sso_providers`):

- the name the sign-in page shows, a preset (Microsoft Entra ID, Google Workspace, Okta, another), the discovery address and the client identifier;
- the allowed email domains, each belonging to one provider of the tenant;
- the claim names for the email, the name and the groups;
- whether it creates people at their first sign-in, with which default role and home unit;
- whether everyone linked to it signs in only through it;
- whether its assertion of multi-factor authentication stands for this product's second factor;
- the group mapping.

**The client secret lives only in the store**, at `tenants/<slug>/sso/<provider id>`. It is written before the row's transaction commits, so a store that refuses leaves no provider without its secret, and deleted after the provider is.

- The fleet's unit policy now also manages `tenants/<slug>/sso/*`: create, read, update and delete there, and nothing else beside what it read before. The policy is written again on every update, so existing units gain it with `make update-unit`.
- A host with no store, run from source, keeps the secrets in its process only and says so in its log; never in the database.
- No row, log line, audit event or answer holds the secret. An audit event says `set`, `kept` or `replaced`. A test reads every text column of the unit's database for it.

**Managing providers** needs the new `identity.sso.manage`, rated critical (it decides who may sign in as whom), and every change a recent sign-in (TF-248). Every change is audited with the fields that changed.

**Test connection** reads the discovery document and the keys afresh and answers each check in words, as passed, a warning or failed: the document and its issuer, the endpoints, the signing keys, the code flow, PKCE with S256, the signing algorithms, sign-out at the provider, and that the client is proved by the first sign-in.

**The presets** fill the discovery address: Entra ID from the directory (tenant) ID, Okta from its domain, Google's fixed. Any standard provider works as "another".

### The flow and its checks

- **The authorization code flow with PKCE (S256), `state` and `nonce`.** `POST /auth/sso/start` makes all three and keeps them in a sign-in challenge of five minutes, held by the HttpOnly sign-in cookie (ADR-0047), and answers the provider's address.
- **The provider sends the browser back to the shell's `/sso/callback` page**, which posts the code and the state to `POST /auth/sso/callback`. The challenge is ended at once, so an answer works once, and the state must match in constant time.
  - A page posting from the tenant's own origin carries the SameSite=Strict cookie, where a redirect from the provider's site to the API would not.
  - The code in the page's address is useless without the verifier, which never left the unit, and the client secret.
- **The code is exchanged** with the verifier, the redirect URI and the client secret, in the form unless the provider takes only Basic authentication.
- **The ID token is checked in full** (OpenID Connect Core 3.1.3.7):
  - signed by a key the provider publishes, with an asymmetric algorithm (RSA, RSA-PSS or ECDSA; never a shared secret or none);
  - the provider's issuer, which the discovery document must state as its own address;
  - this tenant's client among its audiences, and as the authorized party when there are several;
  - not expired, and issued neither in the future nor more than ten minutes ago, on the unit's clock with two minutes' tolerance;
  - the sign-in's nonce.
- **Keys are read again** when a token names a key the unit's copy lacks, a provider having rotated, at most every thirty seconds. Documents and keys are otherwise kept for an hour.
- **HTTPS is required** for every provider address, except loopback names (`localhost`, `*.localhost`, `127.0.0.1`), which only a test provider can have.
- The redirect URI is `https://<the tenant's hostname the request came to>/sso/callback`, and a hostname the tenant does not have is never echoed.

### Who it signs in

In order:

1. **Only a verified email of an allowed domain** matches or creates anyone. A missing email, an email the provider does not mark verified (`email_verified`, `true` or the string `"true"`), or one of another domain is refused and audited (TF-014 AC3, AC8).
2. **A person already linked** to the provider is found by its subject (`sub`), which never changes, even when their email changed there.
3. **An existing person** whose login identifier or email is the verified address is linked at that first sign-in. The link is a security event of theirs and an audit event. An invited person is activated: the provider proved the address the invitation went to.
4. **A new person** of an allowed domain is created only where the provider creates people (TF-014 AC1): active, with no password, in the provider's default home unit (the root without one), with its default role there and below. One transaction with the link and the audit events.
5. Otherwise the sign-in is refused and audited: nobody has the account here.

**A deactivated person is never re-created or reactivated** by single sign-on, linked or matched by email: the sign-in is refused and audited (TF-014 AC9).

### Signing in only through a provider (TF-014 AC2)

- A person is held to it when an administrator marked them (the user record's Sign-in tab, behind a recent sign-in), or when a provider they are linked to says so for everyone it links. Marking someone no provider can sign in is refused, since they would be locked out.
- **A password sign-in is refused with "Use your company login"**, with the provider named for the page's button. This is decided before any password is looked at, nothing is counted, and the answer takes the same hashing time.
  - This tells whoever types the identifier that it belongs to such an account. It is an account no password opens, so the answer reveals nothing a guesser could use. Every account a password does open keeps TF-011's one answer, and an identifier nobody has answers "The login details are not correct", as before.
- **Recovery links do not apply**: issuing one, by an administrator or the operator, and using one issued before the mark are refused. T5's break-glass account is the exception the tenant keeps.
- **The sign-in page** shows a button per provider. A work email typed without a password goes to its domain's provider, decided by the domain alone (`POST /auth/sso/discover`), so the answer is the same for every email of a domain, account or not.

### Roles from groups, within the escalation rule

- A mapping names a group, a role and a scope: the whole tenant, or a unit with or without the units below it.
- **A mapping, and the default role, may only name a role its author may grant at its scope**, by ADR-0031's rule 1 as Authorization reads it (`IProvidedAssignments.MissingToGrantAsync`): `authz.assignments.manage` and every permission of the role there. A refusal is audited and names what is missing.
- **Every sign-in brings the mapped roles in step**: the roles of the person's groups are added, the provider's others removed. Each such assignment carries its source (`sso:<provider id>`, a new column of Authorization's assignments), so a sync never touches a role a person granted, and the user record says where it came from. The last active tenant administrator keeps the role. A role or unit gone since the mapping was saved is skipped and logged.
- The sync is trusted, as the seed's path is: the rule was applied when the mapping was saved, as ADR-0031 applies it when a role is granted.
- Removing a provider removes its links and the roles its mapping gave. The people stay.

### Multi-factor authentication through the provider

- A per-provider setting, on by default: **trust the provider's assertion** when the ID token's `amr` names `mfa` (RFC 8176, as Entra ID and Okta write it). The session then records `provider_mfa` as its second factor, and the required step of ADR-0047 does not ask that session to enrol.
- Without the claim, or with the setting off, **the tenant's MFA policy applies as after a password**: a person with a factor gives it at the second step, and a person who must have one and has none enrols. The provider rides through the step into the session, and no password's age is asked.

### Signing out, and confirming it is you

- Signing out ends this product's session. **When the session came through a provider that publishes an end-session endpoint**, the sign-in page then offers to sign out there too (`GET /auth/sso/{id}/sign-out`, with the client and the way back to `/login`).
- **A person with no password confirms a high-risk action with a true re-authentication at their provider** (TF-248 AC6, decided at the review):
  - The dialog asks for their second factor here first, when they have one, as it would with a password; a wrong one counts as at sign-in. `POST /api/v1/me/reauthentication/sso` then starts a sign-in at the provider that asks for a fresh one: `prompt=login` and `max_age=0`. Its challenge belongs to the session.
  - The answer comes back through `/sso/callback`, checked as a sign-in's is, and counts only when it is the session's own person, signed in to the same provider account, and the ID token's `auth_time` falls within the step-up window (five minutes, the default of `RequireRecentAuthentication`). Then the session's authentication time moves to now, with no new session, and the confirmation is audited.
  - A provider that answers from its own session without asking, an `auth_time` older than the window, or none at all fails with `identity.sso.reauthentication_stale` and a message that the company login did not ask for a new sign-in. Nothing is confirmed and the action stays refused.
  - Every refusal of a re-authentication is a 403, which keeps the session, and carries the page to return to. Either way the person comes back to the page they were on, which says how it went; confirmed, the action is ready to take again. The refused request is not resent, since the provider's page took the browser away.

## Options considered

- **Dex as the test provider**: not chosen. It is the lightest, but its claims are fixed, and the tests must prove unverified emails, groups and `amr`.
- **OpenIddict's client, or Microsoft's OpenID Connect handler**: not chosen. Both expect providers registered at start-up and an authentication handler with its own cookies, where a tenant's providers are rows that change while the unit runs. The flow and its checks are short, and `Microsoft.IdentityModel.JsonWebTokens`, already used for this product's tokens, checks the signature.
- **The provider's answer posted straight to the API** (a server callback, or `form_post`): not chosen. The SameSite=Strict sign-in cookie does not travel on a cross-site redirect, and loosening it to Lax for every sign-in would weaken the second step too. The shell's callback page posts from the tenant's own origin, and goes on to the second step and enrolment as after a password.
- **"Use your company login" for an unknown identifier of a provider's domain**: not chosen. It would tell apart the accounts of that domain that do open with a password, a break-glass account among them, which are the ones worth guessing at.
- **Taking `hwk`, `otp` or other `amr` values for multi-factor authentication**: not chosen. One factor of another kind is not two. `mfa` is what the providers in view write.
- **The client secret encrypted in the database**: not chosen. TF-073 puts every client secret in the store, and the store already holds the unit's other secrets.
- **Any new sign-in at the provider counting as a re-authentication**, as proposed for the review (a new session, `auth_time` unread): not chosen at the review. A provider may answer from a session of its own without asking anyone, which says nothing about who is at the keyboard now.
- **Resending the refused request** after the provider's page: not chosen. The page is gone by then; the person comes back to it and takes the action again, with the session confirmed.
- **Back-channel logout**: a later item, as the task says.

## Consequences

- A tenant connects its providers and tests them without an operator. A new employee signs in on their first day, in the right unit, with the role the provider gives, and a provider that leaves people in or out of its groups changes their roles at their next sign-in.
- A provider can give no more than its author could grant, and its secret is nowhere but the store.
- The sign-in page and the API keep TF-011 for every account a password opens. An account that signs in only through a provider is told so, which reveals an account no password opens. T9's rate limits cover that answer too, so it cannot be asked of identifier after identifier at speed (decided at the review).
- The stack has a test provider, and the smoke and the browser tests sign in through it on every run.
- To revisit:
  - back-channel logout, and ending a person's sessions when their provider says so;
  - a provider per unit (TF-014's outline mentioned it; T4 builds one per tenant, as the sharpened task asks);
  - SAML (TF-015) and SCIM (TF-016) in S7, which will add providers of other kinds beside these.
