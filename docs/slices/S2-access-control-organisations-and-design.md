# Slice S2: access control, organisations and the design system

The second slice (PRD section 7) with one addition the owner made on 2026-10-18: the design system is built in this slice, ahead of the screens that need it, so that S2's screens are the first pages drawn in the design language (ADR-0024). S2 is built with Claude Code from `docs/prompts/S2.md` (ADR-0023).

## 1. What S2 proves

A tenant administrator shapes who may do what: organisation units in a tree, tenant roles made of permissions, assignments scoped to the tenant, a unit or a record, record-level policies that modules declare and lists apply, delegation of duties with both identities recorded, and access reviews. The control plane decides what a tenant has: entitlements by module and feature with seats and plans, feature flags with scopes and kill switches, settings declared in code and resolved through a hierarchy, all visible to the shell and the API within a minute. And every one of those screens looks like the design language says: calm in the work, alive at the edges, keyboard-first, AA, in one drawn hand, with a mark of its own per tenant.

Done when, on the laptop: a second administrator in a child unit sees only that unit's users; a role without `identity.users.read` makes the Users page disappear from navigation and the API answer 403; a plan applied in the control plane hides a module within a minute; a kill switch shows its banner; a setting overridden for a unit resolves for a user in it; a delegate acts and the audit shows both; the gallery scan reports zero AA violations; and the login page of `acme` and of `bravo` each carry their own mark.

## 2. Requirement coverage

| ID | Requirement | What S2 builds | Continues in |
|---|---|---|---|
| TF-021 | Inactivity | The shell warns before the session expires | S3 |
| TF-032 | Roles | Tenant roles with permissions, scoped to a principal type; system roles read-only; export and import | done |
| TF-033 | Scoped assignments | Scope: tenant, unit with or without children, record; effective permissions per record scope | done |
| TF-034 | Organisation units | The unit tree; home unit per principal; access to other units | TF-223 (lifecycle) |
| TF-035 | Record policies | Policy interface per module, evaluated on reads and applied as list filters; unit tests per policy; build check | products add theirs |
| TF-036 | Segregation of duties | Conflict rules between permissions or roles, warn or block, report | Should; last in order |
| TF-037 | Delegation | Duties, date range, reason, both identities in audit, overlap and cycle refused | S5 (approval duties) |
| TF-038 | Access reviews | Campaign per unit, confirm or revoke, decisions recorded, overdue escalation | S4 (notifications) |
| TF-039 | Effective access explanation | "Why can this principal do this": roles, scopes, policies, entitlements, flags | Should; last in order |
| TF-040 to 043, 045, 046 | Entitlements | Modules and features per tenant with status and dates; seats; plans applied with a diff; one effective-entitlement service with a 60-second cache in UI and API; audit and history | TF-044 trial and grace (Could) |
| TF-050, 051, 052, 054, 056 | Feature flags | Types and scopes, OpenFeature in API and UI, in-house provider, kill switches with banner, lifecycle check | TF-053, 055 (Should, Could) |
| TF-057 to 059, 221 | Settings | Declared in code, hierarchy user, unit, tenant, system; console with audit and reset; typed accessor; effective settings to the UI; organisation scope | done |
| TF-222 | Cross-organisation access | Assignments to several units or a parent with children; sharing with listed units; cross-unit read audit; organisation switcher | S8 (supplier) |
| TF-223 | Organisation lifecycle | Create with checklist, move, deactivate; merge and split | Should; merge and split last |
| TF-170 | Shell | Navigation filtered by entitlement and flags; header with search, notifications, help, profile; page patterns | S4 (notifications content) |
| TF-171 | Design system | Tokens, illustrations, patterns, page templates, forms with server error mapping, panels, dialogs, states, saved views | S5 (saved views on the grid) |
| TF-172 | Accessibility | AA on the gallery, scan in CI, keyboard operation of the grid, no colour-only status | quarterly audit |
| TF-173 | Theming | Tenant mark, colours within contrast rules, logo, name | S4 (emails) |

## 3. Design

### 3.1 Two streams, one order

Stream D (design) runs first and then alongside: D1 to D3 before any A screen, D4 after A2 when there are enough pages to make a home. Stream A (access control and organisations) builds its API and its page in the same task, on the templates of D3. Each task is one Claude Code session from its section in `docs/prompts/S2.md`.

### 3.2 Where things go

- Authorization grows: `kernel_authz.organisation_units`, `kernel_authz.role_assignments.scope_*`, `kernel_authz.policies` stay in code, `kernel_authz.delegations`, `kernel_authz.access_reviews`. The evaluator takes a scope. The tree is a kernel mechanism; its kinds are an extension point that `Iron.Business.Organisations` fills later (ADR-0029).
- A new kernel module `Iron.Kernel.Entitlements` (schema `kernel_entitlements`) in the tenant unit reads what the control plane decided; the control plane holds plans and tenant entitlements in `Iron.Kernel.Tenancy`. The unit learns of a change through its secret and a 60-second refresh, so no unit calls the control plane on a request.
- A new kernel module `Iron.Kernel.Flags` (schema `kernel_flags`) with the in-house OpenFeature provider; the shell's OpenFeature client takes the evaluated flags from `/api/v1/me/context` (flags, entitlements, effective settings, the organisation switcher's units) in one call.
- A new kernel module `Iron.Kernel.Settings` (schema `kernel_settings`): declarations in code, values per scope, the typed accessor generated by a source generator from the declarations.
- The shell gains `src/design/` (tokens, illustrations, patterns), `src/components/templates/` and the panels, a `gallery` route in development builds, and the tenant console pages under `src/modules/kernel/console/`.

### 3.3 Decisions to record as they are made

Scope evaluation performance (unit tree closure table against recursive queries), the entitlement push (secret refresh against a control plane event), the settings accessor generator, the OpenFeature provider shape, and the illustration review rule: one ADR each, written in the task that decides it.

## 4. Task breakdown

| Task | Name | Scope | Done when | State |
|---|---|---|---|---|
| D1 | Design foundation | Tokens (ink, paper, accents, type scale, space, radius, motion), the Mantine theme on them, dark scheme, the density switch, the gallery route, the accessibility scan with axe in Playwright on the gallery and in CI; login page and layout restyled on the tokens; a lint rule against raw colours in pages | The gallery shows every token and component with zero AA violations in CI; the login and the layout use no colour by value; reduced motion honoured | done |
| D2 | The drawn world | The pattern generator and the tenant mark; the illustration set: four empty states, three errors, login, home, success, loading; the illustration review checklist; applied to the login page, the error views, the grid's empty state | `acme` and `bravo` show different marks on login; every empty and error state carries its drawing; every drawing has its accessible name or is hidden; the gallery scan passes | done |
| D3 | Templates, panels and forms | List, record with tabs and record header, wizard, console templates; empty and loading states; dialogs; the schema-driven form with inline validation and server error mapping; the Users page on the list template; a sample record page in the gallery | A server validation problem lands on the field (TF-171 AC3); the Users page uses the template; keyboard operation of the grid proven in a browser test | done |
| P1 | The laptop store survives a restart | OpenBao in server mode on a volume with the static seal, initialised by make up; the update job heals a unit whose secrets were lost; the seed sets a healed service account's new secret; the smoke and the browser tests take a tenant (ADR-0028) | make down and make up keep every secret; a unit whose secrets were deleted is healed by make update-unit; a fresh tenant passes the smoke and e2e | done |
| L1 | Three layers: kernel, business, products | The layers in names, folders, schemas and build checks before any further module (ADR-0029): `Iron.Kernel` as one namespace, `Iron.Kernel.<Name>` under `src/Kernel/`, hosts under `src/Hosts/`, `<IronLayer>` in every project, the downward rule in the architecture tests and in ESLint, `kernel_` schemas from a new migration baseline, the empty Business and Products layers, the laptop reset once | An upward reference and a missing layer fail the build; the schemas carry their prefix; fresh tenants pass smoke and e2e after the reset | done |
| A1 | Organisation units | The unit tree with home units, the unit API, the tree page on the record template, the `unit` scope in the evaluator, TF-034 | A principal with a home unit; the tree page lists and moves units; a scoped assignment to a unit resolves for records owned by it | done |
| A2 | Roles and scoped assignments | Tenant roles CRUD with export and import, system roles read-only, assignments with the three scopes, the role editor and the assignments page, effective permissions per record scope, TF-032, TF-033 | A role without `identity.users.read` removes the page and the API answers 403; a child-unit administrator sees only its users | done |
| A3 | Record policies | The policy interface, evaluation on reads by id, filters on lists, the owner and unit policies, the build check that every list of a business record applies its policy, TF-035 | A list without a policy fails the architecture test; the unit policy proven by test and by the Users page | next |
| D4 | Home and the header | The home page with module openers, section openers, the header with search, notifications, help and profile, the session expiry warning, TF-170, TF-021 | Home is judged by the brief's six questions in the pull request; the warning appears two minutes before expiry | |
| A4 | Entitlements | Plans and tenant entitlements in the control plane with a diff before apply and audit; seats; `Iron.Kernel.Entitlements` in the unit with the 60-second cache; navigation and API checks; TF-040 to 043, 045, 046 | A plan applied in the control plane hides a module within a minute; the API refuses its operations; seats above the count refused | |
| A5 | Feature flags | `Iron.Kernel.Flags`, OpenFeature in API and shell, the in-house provider with scopes, kill switches with the banner, lifecycle check, TF-050, 051, 052, 054, 056 | A kill switch stops its path within a minute and shows the banner; a flag removed from code with rules defined fails the build | |
| A6 | Settings | `Iron.Kernel.Settings`, declarations, hierarchy with the organisation scope, the generated accessor, the console page with audit and reset, effective settings to the UI, TF-057 to 059, TF-221 | A setting overridden for a unit resolves for a user in it; a change applies without restart; the console shows who changed what | |
| A7 | Delegation and access reviews | Delegations with duties and dates, acting-as with both identities in audit, overlap and cycle refused; review campaigns, TF-037, TF-038 | A delegate's action records both identities; an overdue review is reported | |
| A8 | Cross-organisation access and lifecycle | Assignments to several units, sharing with listed units, cross-unit read audit, the organisation switcher, create, move, deactivate, TF-222, TF-223 | A private record of another unit is refused without sharing and audited when shared | |
| A9 | Explanation and segregation of duties | "Why can" endpoint and page; conflict rules with warn or block, TF-039, TF-036 | The explanation names the role, scope, policy, entitlement or flag that decided | |
| S2-close | Smoke, docs, done | Smoke steps for scope, entitlement and kill switch; docs; the closing notes | `make smoke` with the new steps; the done-when of section 1 | |

## 5. How we build

Each task is one Claude Code session from its section in `docs/prompts/S2.md` (ADR-0023). The result is reviewed in the design session from the diff and the test output; fixes go back as the next prompt. A task is done when its "done when" holds on the laptop, its tests and documentation are in, and its ADRs are written. The State column of the task table is updated as tasks land.
