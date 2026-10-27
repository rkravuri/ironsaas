# ADR-0053: Hardening the web surface: headers without inline code, origins, declared limits, rich text, SQL, rate limits, the login's challenge and the control plane's own role and chain

**Status:** Proposed, for the design session's review
**Date:** 2026-10-28
**Decided by:** Product owner and architect, in the brief of S3 T9a; the details here are the task's

## Context

S3 T9a hardens what faces the network (TF-070 to TF-072) and takes three items parked for it: the shell's server answering its own page for the API's paths (M4's later item), the control plane connecting as the database's superuser, and the control plane's own audit trail, which nothing sealed (T6's later items).

It rests on the security defaults of S1 (ADR-0010), the REST conventions and their problems (ADR-0009), the module boundaries (ADR-0001), settings (ADR-0036), the sealed audit chain verified from outside the unit (ADR-0050), the unit's split into an owner role and an app role (ADR-0018), and the rule that every test names what it proves (ADR-0044). The brief set five checks: no `unsafe-inline` anywhere, or a stop and a report; every request field limited, enforced by the build; SQL from strings failing the build; every limit answering 429 with `Retry-After` and a CAPTCHA with no external host; and a control plane that cannot change its own sealed trail, verified by a separate role.

## Decision

### 1. Headers, and a policy without inline code (TF-070 AC1, AC3, AC4)

- **Every answer of every host carries the headers**, whatever its path or status: a content security policy, `Strict-Transport-Security`, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, a referrer policy, a permissions policy, and `Reporting-Endpoints`. HSTS is sent even when the ingress speaks plain HTTP to the host: browsers ignore it there, and it cannot be forgotten behind TLS.
- **No `unsafe-inline` for scripts or styles, anywhere.** The API's and the control plane's policy allow their own origin only. The shell's style elements carry a nonce: Vite leaves a placeholder on its tags and in a `csp-nonce` meta tag, nginx replaces it with the request's id in the page and in the policy's `style-src`, and the shell hands the nonce to Mantine (`getStyleNonce`) and to AG Grid (`styleNonce`). No library needed inline code, so nothing was weakened and nothing had to be reported.
- **The shell's headers live in one snippet every nginx location includes**: nginx drops an outer level's headers wherever a location adds its own, which had left the page's own answers without them.
- **Violations are reported** to `/api/v1/csp-reports` in both forms (`report-uri`, and `report-to` with `Reporting-Endpoints`). The endpoint counts each report by directive (`protection.csp_reports`), keeps the latest 100 without their query strings for the console (`protection.csp_reports.read`), logs at most 20 a minute, reads at most 64 KB, and takes 60 reports a minute per address; more are refused unread and counted (`protection.csp_reports_dropped`).
- **Cookies are Secure, HttpOnly and SameSite=Strict by policy**, whatever the code that writes them asks, and no code writes a `Set-Cookie` header around the policy; a test checks both.
- **The shell's server answers 404**, as a problem, for `/api`, `/auth`, `/connect`, `/health` and `/.well-known`, and for a file that is not there. The single-page fallback serves the shell's own routes only.

### 2. Origins per tenant (TF-070 AC2)

- **`protection.cors.allowed_origins`**, a tenant setting, lists the other sites that may call the tenant's API from a browser: empty by default, at most 20, each a scheme and a host with an optional port, without a path, HTTPS except for this machine (`localhost`, `*.localhost`, loopback). Changing it needs a recent sign-in.
- **The CORS check reads it at each request**, with the operator's own origins (`Security:Cors:AllowedOrigins`) beside it. A listed origin gets the CORS answer with credentials and the exposed `X-Correlation-Id` and `Retry-After`; any other gets none, so the browser withholds the response. Settings shows the list one origin at a time.

### 3. Every request field has a declared limit, and the build enforces it (TF-071 AC1)

- **A start-up check (`RequestLimitCheck`)**, beside the permission check, reads each endpoint's body type through its validator. A string has a maximum length; a list a maximum count (`MaximumCount`) and its items their own limits; a free JSON value a maximum size (`MaximumJsonSize`); an object a validator of its own whose fields pass the same rules. Numbers, dates, identifiers, flags and enumerations are limited by their types, so a body of only those needs no validator. A body the handler reads itself declares its size (`LimitBodySize`), which the server enforces. The host does not start otherwise, naming the endpoint and the field; the host tests start every host, so a field without a limit fails the build.
- **The limits are mostly the columns' own**; those of fields that pass through (login id, password, codes, a passkey's answer) are `FieldLimits`, shared by the modules.
- **The query is limited for every request**: at most 50 parameters, each at most 2,000 characters. Route values are parts of the path, which the server limits.
- **A request over any limit is a validation problem naming the field**, in the API's usual shape.

### 4. Rich text keeps its formatting and loses its scripts (TF-071 AC2, AC5)

- **`RichText`**, in the kernel's security library, holds HTML cleaned by HtmlSanitizer (MIT, on AngleSharp, MIT) as it is made; its JSON converter cleans a request's field as it binds, so a handler never sees a script. Input over 256 KB is refused unread.
- **The allow-list keeps** paragraphs, line breaks, emphasis, lists, quotes, code, headings from the second level, rules, tables and links to `http`, `https` and `mailto`, with `rel="noopener noreferrer nofollow"`. It drops scripts, styles, frames, forms, images, embedded objects, SVG and MathML, event handlers, style attributes, classes, comments and every other scheme. A tag off the list goes and its text stays, except where the text is code or a control, which goes whole.
- **Its length is a validator rule** the start-up check reads like a string's. A sample field proves it through a request; the first real fields come with the business documents (S4).
- **In the shell, HTML reaches a page only through `RichTextView`**, which takes a string only an API binding's schema makes (`richTextSchema`). A lint rule, `iron/inner-html`, refuses `dangerouslySetInnerHTML`, `innerHTML`, `outerHTML`, `insertAdjacentHTML` and `document.write` anywhere else.

### 5. File names are display text (TF-071 AC4)

- A stored file's name is the last part of any path, whichever separator the client used, without control or formatting characters (a right-to-left override hid extensions), without characters file systems refuse, without leading dots or a device's name, in Unicode's composed form, and at most 255 characters with its extension kept. The bytes stay under a random key, as before; the name is never a path.

### 6. SQL is never built from strings (TF-071 AC3)

- **An analyzer for the product's code**, `tools/Iron.Analyzers`, goes into the compile of every Kernel, Business, Suite and Host project. IRON0010 refuses SQL text that is not a compile-time constant where it reaches EF Core's raw methods, a command's constructor, a data source's `CreateCommand` or a command's `CommandText`. Constant text with parameters passes, and so do `FromSql` and `ExecuteSql`, whose interpolated values become parameters.
- **Definitions PostgreSQL takes no parameters for** (roles, grants, databases) are built in methods marked `[SqlFromIdentifiers("why")]`, which a reviewer reads; an exemption without a reason is itself an error (IRON0011). Three methods carry one today: the grants, the fleet's database step, and the control plane's preparation.
- **The analyzer does not follow a constant through a helper's parameter**; such a helper is either made constant at its call or marked with the reason. Tests and tools stay outside the rule: their fixtures build SQL.

### 7. Rate limits (TF-072 AC1 to AC3)

- **Defaults**: sign-in 10 a minute per address (a password, a passkey alone, a service account's token); recovery 5 an hour per account (a recovery link issued for a person, an administrator's recovery for a tenant), or per address when the link is used; the API 600 a minute per principal, or per address before sign-in; exports 20 an hour per principal (the audit export, the roles' export, evidence packs, the validation pack). The "Use your company login" answer is an answer of the sign-in, so it counts. The second factor keeps its own cooling (ADR-0047).
- **A tenant may change them within the operator's bounds** (`Protection:Limits:<limit>:Minimum` and `Maximum`; by default 3 to 60, 1 to 20, 60 to 6,000 and 1 to 200). The limiter reads the setting at each request from the settings' cache, so a change applies within 30 seconds, inside the minute the criterion allows; the window's key holds the value, so a new value starts a new window.
- **A refusal answers 429** with `Retry-After`, the error code `protection.rate_limited`, the seconds to wait and the limit's name. It is counted (`protection.rate_limited` by limit) and audited at low severity, once per window and subject, so a flood writes one event, not thousands. The shell says how long to wait, in seconds under two minutes and in minutes above.
- **The counters are fixed windows in each API process.** S6, with several replicas per unit, moves them to a shared store; until then a unit's limit is per replica.

### 8. The login's challenge (TF-072 AC4)

- **After 5 failed sign-ins in 15 minutes from one address, or for one login identifier**, a password sign-in needs a solved challenge first; the API refuses it with `identity.login.challenge_required` without checking the password. T2's lockout still applies on top.
- **The challenge is ALTCHA's protocol (MIT), in our own code**: a salt with its expiry, the SHA-256 of the salt and a secret number, and an HMAC signature; the browser finds the number and sends it back. The server checks the signature, the expiry (5 minutes) and the number, and takes each answer once. The work grows with the failures: it doubles with each failure past the threshold, up to sixteen times, which is the progressive delay.
- **It needs no other host**, so the policy stays strict. The shell shows the box only when the API asks for it and solves the challenge in a Web Worker of its own origin (`worker-src 'self'`); the widget's own package was not needed for a protocol this small, and would have brought markup and styles to fit to the design language and the policy.
- **The failures and the used answers are counted in each process**, with the challenge's key from `Protection:Challenge:Key` or made at start; S6 moves them to the shared store with the counters, and T9b gives the key its rotation.

### 9. The control plane's own role and chain (T6's later items)

- **Three roles, as a unit has**: `control_owner` owns the `control` database and runs its migrations and seeds; `control_app` is what the control plane's API and the fleet run with, and `migrate` grants it only what they need, append-only on the audit tables; `control_audit` reads the chain's tables and nothing else.
- **The fleet's command `prepare-control`** makes the roles as the platform's superuser, moves a database made before this to the owner with every schema, table, sequence, view and function in it, and writes two secrets: `control/unit` with the app role's connection, and `control-migrate/unit` with the owner's and the two roles to grant. `make secret-control`, `make up-control` and the pipeline run it first; migrate and seed then run with `Secrets__Path=control-migrate`.
- **The control plane seals its own trail** with the unit's sealer, in its API, and anchors each seal in object storage outside its database: on the laptop a container in Azurite, which it reaches only by a SAS that may read, list and create blobs, never write over or delete one. In Azure the container carries an immutability policy: the first Azure deployment's requirement.
- **The fleet verifies the chain daily**, in full monthly, with `control_audit` against those anchors, and records the result beside the tenants' under `control`; a run that does not pass raises the same alert. `make verify-control-audit [FULL=1]` runs it by hand.

## Options considered

- **`unsafe-inline` for the shell's styles**: refused; a nonce per page was possible for every library the shell uses.
- **A hash list instead of a nonce**: refused; Mantine and AG Grid write styles at run time, which no build-time hash covers.
- **CORS origins in the operator's configuration only**: refused; a tenant's own portal is the tenant's decision, within the format the platform checks.
- **DataAnnotations and .NET's built-in validation for limits**: refused; FluentValidation is the one mechanism already used, its descriptors can be read at start, and its problems already name the field in the API's shape.
- **An architecture test reading source files for limits**: refused; reading the validators the hosts actually register catches what source reading would miss, such as a nested object without a validator.
- **Cleaning rich text when it is shown, in the browser**: refused as the only line; cleaning as it is written means no stored record holds a script, and the lint rule keeps the shell to one rendering place.
- **A banned-API list instead of an analyzer**: refused; it cannot tell constant SQL from SQL built at run time.
- **ASP.NET Core's rate limiter middleware**: refused; its policies are fixed at start, and a tenant's limits change at run time and are read per request. The library's `PartitionedRateLimiter` is used underneath.
- **A hosted CAPTCHA service**: refused; it would need another host in the policy and send people's data to a third party.
- **The control plane's anchors in the control database, or in the tenants' units**: refused; anchors must be outside the database they stand for, and a unit is not the control plane's auditor.

## Consequences

- A new endpoint cannot ship a request field without a limit, nor SQL built from a string, nor HTML put into a page outside the one component; each fails the build with the place named.
- Every failed sign-in costs an attacker more work, without a third party; a person sees one box, only after repeated failures.
- The shell's page cannot run injected script or style even if one got into a record; the reports say where a policy was hit.
- The control plane can no longer change its own schema or sealed events, and its chain is checked daily by a role that can change nothing.
- To revisit: S6 moves the rate counters, the challenge's failures and used answers to a shared store; T9b rotates the challenge's key with the others; the first Azure deployment puts an immutability policy on the control plane's anchors container, and gives the fleet a secret of its own, apart from the API's, which today still shares `control/unit` with the fleet's admin connections. The query's limits are one bound for every endpoint; an endpoint that needs more declares it when one does.
