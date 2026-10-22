# ADR-0049: Sessions, revocation, impersonation, break-glass, emergency accounts and terms

**Status:** Accepted
**Date:** 2026-10-23
**Decided by:** Product owner and architect, at the design session's review of S3 T5 (2026-10-23)

## Context

S3 T5 builds six requirements about who is signed in and on whose behalf: the sessions console (TF-022), the concurrent-session limit left from S2 (TF-021 AC2), logout and revocation (TF-029), impersonation with attribution (TF-025), break-glass access for operators (TF-026), and terms acceptance (TF-028). It also builds the tenant's own emergency accounts that ADR-0048 promised for when a customer's identity provider is down. It rests on the session of ADR-0014 and ADR-0033, the step-up of ADR-0047 (TF-248), the escalation rule of ADR-0046, the operator's handover through the store of ADR-0046, the audit trail of ADR-0013 and its two people of ADR-0037.

## Decision

### Sessions

- **Each session records how it signed in, on what, and from where** (TF-022): its method (`password`, `passkey`, `sso`, `impersonation`, `break_glass`), the browser and platform read from the user agent by a small reader of our own ("Chrome" and "Windows", which the shell words as "Chrome on Windows"; an agent it does not know is named by nothing rather than guessed), and the city and country of the client's address.
- **The client's address** is the connection's when it is public. Behind the ingress, whose address is private, it is the last address of `X-Forwarded-For`, the one the ingress added; a header from a public peer is the client's own claim and is ignored.
- **The location database is DB-IP's "IP to City Lite"** in its MMDB form (CC BY 4.0, `docs/licences.md`), read with MaxMind.Db (Apache-2.0). The API image carries it at `/app/locations/dbip-city-lite.mmdb`: its build downloads one pinned month and checks the file's SHA-256, so the build is repeatable and nothing is fetched at runtime. DB-IP keeps a few months online; when the pinned one goes, the build says so and the pin moves to a newer month (the runbook). A missing file means no locations; sessions work the same. Its licence asks for a link back wherever its data shows: "IP Geolocation by DB-IP", linked to https://db-ip.com, stands under every list of session places, in My profile and in the console.
- **Only the city and the country are kept with the session.** The full address goes into the sign-in's audit event and lives as long as the audit trail does; nothing else stores it.
- **A person lists and ends their own sessions** in My profile: any but the one they use, which signing out ends, or all the others at once ("Sign out everywhere else"). A session in which someone else is signed in as the person is not theirs to end.
- **An administrator with `identity.sessions.manage`** (high risk) lists everyone's live sessions first, at the page's full width with the person, the device and the place readable at 1280 pixels (decided at the review), sees how many people are signed in now and a summary of recent UTC days (sessions started, the peak of people signed in at once, sessions an administrator ended), and ends any session with a reason, behind step-up. The person's security activity and the audit trail name who and why. A person's overlapping sessions count once in the peak, and a session counts until it ended or until its idle or absolute limit if nobody ended it sooner. Before listing, the console ends the sessions nobody came back to after their limits, as their next request would have.
- **The 401 says why a session ended**: `auth.session_limit`, `auth.session_ended` (an administrator), `auth.session_revoked`, `auth.impersonation_ended` (an operator's), `auth.token_revoked` for a token, and `auth.session_expired` for the rest. The login page says it in words. A session ended because its person declined the terms ends as they asked, and the login page says that too.
- **Every answer of a live session says when it ends without activity** (`Iron-Session-Idle-Ends`), so the shell's warning is exact when a tenant shortens its limits (folded into T5 from ADR-0033).

### The concurrent-session limit

- `identity.session.concurrent_limit`, a tenant setting from 0 (no limit, the default) to 10 (TF-021 AC2). A person's sign-in past it ends their oldest live sessions, each audited; each hears why at its next request. Sessions in which someone else is signed in as the person neither count nor end.

### Revocation

- **Logout ends the session on the server and clears the cookie** (TF-029 AC3). Every answer under `/api`, `/auth` and `/connect` carries `Cache-Control: no-store`, and the shell forgets every cached answer when a session ends, so the back button shows no protected page.
- **Each principal and the tenant carry a "valid after" time** (TF-029 AC2). Revoking a person or a service account sets theirs; revoking the whole tenant records a revocation, whose latest time is the tenant's. A session started before either, and a token issued before either, is refused.
- **A principal's time costs nothing to check**: its row is read on every request already. The tenant's is kept by each API process for at most 60 seconds; the process that took the revocation knows it at once. So sessions and tokens stop within one minute everywhere (TF-029 AC1). A revocation also ends the sessions it knows at once.
- **A token's issue time has whole seconds**, so a token issued in the second of a revocation, before or after it, is refused: the safe side; its client asks again a second later.
- **Revoking needs a reason and step-up**: a person's or a service account's on the user record with `identity.users.manage` at their unit (nobody revokes themselves; they sign out everywhere), the tenant's in the console with `identity.sessions.manage` and the tenant's identifier typed to confirm. The tenant's ends the asker's own session too: everyone signs in again. It is audited at high severity.

### The audit trail

- **Every event written while someone is signed in as another person names the impersonator** (TF-025 AC2): `impersonator_id` (reserved since S1) and, in the details, their name, filled by the writer from the request, without the module saying so. An operator is nobody in a unit, so their id is the control plane's, and the name in the details stands for them in the list.
- **Events carry a severity**, `normal` or `high`; `high` marks what an administrator must look at: an emergency account's sign-in, a tenant-wide revocation, a break-glass access.
- **A module that must account for a principal's actions reads them through `IAuditTrail`**: the review that ends a break-glass access uses it.

### Impersonation

- **Within the tenant** (TF-025): `identity.impersonation.start` (critical) at the person's home unit, a reason (TF-025 AC1) and step-up; only an active person, not oneself, and only someone whose permissions the impersonator holds (ADR-0046's rule, TF-025 AC4). The duration is chosen up to `identity.impersonation.max_minutes` (15 to 120, default 120).
- **It runs in a second cookie, `iron.impersonation`**, over the impersonator's own session, which stays and is kept alive. The request runs as the person, with the impersonator named. At its time, the next request ends it, clears the cookie, says so (`Iron-Impersonation-Ended`) and runs as the impersonator again (TF-025 AC3). "End" in the band does the same at once.
- **The operator impersonates through the control plane** (`tenancy.impersonation.start`, critical, with a reason and a fresh token), for a tenant whose `identity.impersonation.operator_allowed` is on (the default). The fleet runs the unit's `impersonate` command, which hands a one-time sign-in link over through the store, as recovery links are handed over (ADR-0046). The operator has no session to return to: at the end the next request is refused with `auth.impersonation_ended`.
- **Never during impersonation**: step-up (the recent-authentication check refuses with `auth.impersonation_forbidden`), so nothing behind step-up either; electronic signatures (T7) and second approvals (T8), which ask the same check; and the person's own factors, password, sessions, terms and acting for someone, whose endpoints are marked `ForbiddenDuringImpersonation`. Step-up would let an impersonator prove an identity that is not theirs, and Part 11 requires a signature by the real person. A required step of the person's (enrolment, terms) is not asked of an impersonator either: it is the person's to take.
- **The person sees it** in their security activity: who, when, why, and when it ended (TF-025 AC6). The email waits for S4.

### Break-glass

- **An operator requests access to a tenant** in the control plane with a reason, a ticket reference and a duration (60 minutes by default, 240 at most). **A second operator, not the first, approves with a fresh token** (step-up for a client, ADR-0047). A request not approved within 30 minutes expires (TF-026 AC2); the fleet host checks every minute, and an approval of an expired request is refused.
- **On approval the fleet runs the unit's `break-glass-open` command**: it creates a time-limited account with the predefined role `break-glass` and hands a one-time sign-in link over through the store to the operator who asked. The tenant's audit trail names both operators, the ticket and the reason, at high severity. The account is a person's account in the users list, marked as break-glass, with no password; its sessions need no enrolment, since the two operators and the control plane proved who it is.
- **The role `break-glass` is defined in code**: every permission of the tenant but the critical ones. Break-glass repairs a tenant; it does not change who signs in through which provider, or sign in as someone else. **The critical permissions stay with the tenant's own administrators and its emergency accounts** (decided at the review): what break-glass cannot do, such as connecting a company login, granting a critical permission or signing in as a person, waits for someone the tenant itself trusts, who signs in with a password and a second factor even when the provider is down.
- **At the time limit, or when an operator ends it early**, the fleet runs `break-glass-close`: the account is deactivated, its sessions end, and the review record is made from the audit trail, every action of the account in order (TF-026 AC3). The unit also refuses the account after its time by itself, should the fleet be late. Until S5's task engine the review is a record in the control plane, with the full list, and an event in the tenant's audit trail.
- **The control plane seeds a second operator** (`operator-two`, its secret from the laptop's environment as the first's) for the laptop and the tests. `make break-glass-request`, `make break-glass-approve`, `make break-glass-link`, `make break-glass-end` and `make break-glass-show` drive it, and `make impersonate` the operator's impersonation (the runbook).

### A tenant's emergency accounts

- **An administrator may mark at most two local accounts** as emergency accounts, behind step-up and the escalation rule. They must have a second factor, whatever the policy says; they are exempt from SSO-only, so they sign in with a password when the tenant's identity provider is down.
- **Every sign-in with one raises an alert**: a security event of the person's, an audit event at high severity, and a banner for the tenant's administrators for the next 24 hours.

### Terms

- **Documents have versions.** The platform's terms of service and privacy notice are the operator's, published in the control plane (`tenancy.documents.manage`, `make platform-document` on the laptop) to the store (`platform/documents`), which every unit's policy may read and each unit keeps for at most 60 seconds. A tenant's own documents are its administrators', in its console, with `identity.documents.manage`.
- **A tenant document can be retired**: nobody is asked to accept it any more, and its acceptances stay. Retiring is open during the terms step, so an administrator who published a document by mistake, and is held by it too, can take it back without accepting it.
- **A new version must be accepted before anything else** (TF-028 AC1), as T3's enrolment is (ADR-0047): a required step, `identity.terms.acceptance_required`, which only the terms' own endpoints, `/me`, the context and logout pass. An endpoint names the steps it belongs to (`AllowDuringRequiredStep(step)`), so the terms step does not open the enrolment's endpoints, nor enrolment the terms'. Terms come before enrolment when both are pending. Each acceptance stores the document, the version, the time and the person (TF-028 AC2); administrators list them and export a CSV.
- **Declining ends the session** (TF-028 AC4); the next sign-in asks again.
- **No automated run accepts a document** (decided at the review): the smoke never does, and the pipeline never publishes one. On the laptop, `make accept-documents TENANT=x` accepts the current versions as the stored test administrator, run deliberately after `make platform-document`, so the smoke can sign in again. **A service account never accepts** (TF-028 AC3): it signs in with tokens, which a required step never stops. Neither does an impersonator or a break-glass account accept for anyone.

### The user record's actions

- **At most two everyday actions stay in sight** (decided at the review): unlocking a locked login, reactivating, and issuing a recovery link, in that order. The rest wait under More: signing in as the person, resetting factors and the emergency mark, then, last, apart and in the danger colour, deactivating and revoking everything, each still behind its confirmation.

## Options considered

- **A user-agent parser package**: not chosen; the shell needs a name a person recognises, and a small reader covers the browsers people use.
- **The location database in the repository, or fetched by the unit at start**: not chosen; 130 MB in git, or a request to an outside host at runtime. A build-time download with a pinned checksum keeps the repository small and the unit offline.
- **MaxMind's GeoLite2**: not chosen; its licence requires an account and accepting an end-user agreement for every download.
- **Revocation by token deny-list**: not chosen; every token would need a lookup, while a "valid after" time is one comparison on a row read anyway.
- **Impersonation by switching the impersonator's own session**: not chosen; the impersonator's session would have to be rebuilt at the end. A second cookie leaves it as it was.
- **The break-glass account with a password set by the operator**: not chosen; a one-time link, handed over through the store, never leaves anything to remember or leak.
- **Break-glass with the tenant administrator role**: not chosen; it would let an operator change single sign-on and impersonate people, two acts with their own controlled paths.
- **The smoke accepting the documents itself**: not chosen at the review; an automated run that accepts terms would hide whether a person is asked, and accept on someone's behalf.
- **Terms checked at sign-in only**: not chosen; a version published during a session would wait for the next sign-in. The required step applies at the next request, as the MFA policy does.

## Consequences

- Sessions say where and on what they are, and a person and an administrator can end them; a tenant can limit them.
- Revocation is effective within a minute for sessions and tokens alike, at the price of one cached query per request.
- An impersonator and an operator leave both names on every event, and can never prove an identity that is not theirs.
- Break-glass depends on the fleet for its account and its review; the unit's own time limit bounds the access if the fleet is late.
- Later: the email to a person impersonated, and the new-device email, in S4 (TF-019); break-glass's review as a task in S5's engine; the location database's monthly refresh in the release workflow.
