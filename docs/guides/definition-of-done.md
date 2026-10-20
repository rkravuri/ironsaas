# Definition of done

What a change needs before it is merged to main. The pull request template summarises it; this page is the reference (TF-209).

## Every change

- [ ] `make test` and `make shell-check` are green locally and in CI; `make format` (`dotnet format` and Prettier, ADR-0040) makes no change.
- [ ] New or changed behaviour has tests at the level that proves it: a unit test for a rule, an integration test on the container fixtures for anything that touches the database, the store or storage, a shell test for a page or a mapping.
- [ ] No warning was suppressed without a comment that says why.
- [ ] No secret, credential or personal data is in the change; gitleaks in CI is the net, not the first check.
- [ ] Documentation where behaviour changed: the README's first hour if the path changed, the developer guide if a way of doing things changed, the runbook if an operator's command or a symptom changed, the module's README if its responsibility changed.

## When it applies

- [ ] A decision was made (a library, a pattern, a boundary, a trade-off): its ADR is in `docs/adr/`, listed in the index, and named from the code or the guide that applies it.
- [ ] A module was added: the placement test is answered in its README, its projects declare `<IronLayer>` and live in their layer's folder, and its schema carries the layer prefix (ADR-0029).
- [ ] An entity changed: the migration is generated with `make ef-add`, read once, formatted with `make format`, committed with the entity, and the module's model test passes.
- [ ] An endpoint was added: it declares `RequirePermission`, `RequireSignedIn` or `AllowAnonymous`; a new anonymous endpoint is a reviewed addition to the fixed list in `Iron.Host.Tests`.
- [ ] A permission was added: it is in the module's Contracts, in the module's `Permissions`, and seeded into the system roles that should carry it.
- [ ] A licensed module or a feature was added: the module declares its licence and features (a kernel module declares none), the endpoints that need a feature say so, `make catalogue` was run and the catalogue committed (ADR-0034).
- [ ] A flag was added or retired: it is declared in the module's Contracts with its owner and purpose, a release or experiment flag with its removal date; a retired flag is marked so in the catalogue in the same change; `make catalogue` was run (ADR-0035). A kill switch is the kernel's only.
- [ ] A setting was added: it is declared in the module's Contracts with its scopes and rule, named in the shell's catalogue under `settings.names`, and `make catalogue` wrote the catalogue and `shell/src/api/setting-keys.ts` (ADR-0036).
- [ ] A duty was added: it is declared beside the module's permissions, listed in its `IModule`, and in the catalogue (ADR-0037).
- [ ] A business record type was added: its policy is registered with its display name (`AddRecordPolicy`), its lists and reads go through `IRecordPolicies`, and the module registers its records for merges (`AddUnitRecordOwners`) (ADR-0032, ADR-0038).
- [ ] A test needs PostgreSQL, blob storage or the store: its fixture takes them from `tests/Iron.Testing`, in a database, containers and a mount of its own, and anything that lives in a whole server (a role, a container name, a policy) carries the project's name.
- [ ] A browser test makes data on the tenant: it makes it under the run's unit (`E2E_RUN_UNIT_ID`) and names it with the run's id, never under the root.
- [ ] Something a user does changed state: the audit event is written in the same transaction (ADR-0013) and its name is a constant in the module's Contracts.
- [ ] A shell route or page was added: its permission is declared in the manifest, its strings are in the catalogue, `make shell-check` passes, an accessibility spec walks it by keyboard in both schemes with its screenshots, and the brief's six questions (`docs/design/design-language.md` section 9) are answered in the pull request.
- [ ] A shell component, a token or a themed variant was added or changed: it has its example in the gallery (both schemes, both densities) and `make shell-a11y` passes.
- [ ] A drawing or pattern was added: it passes `docs/design/illustration-checklist.md` (one hand, original, accessible name or hidden, both schemes, reduced motion, patterns never behind text), answered line by line in the pull request.
- [ ] A host, an image, a provisioning step or the unit template changed: `make images`, `make update-unit TENANT=<slug>`, `make smoke TENANT=<slug>` and `make e2e TENANT=<slug>` passed on the laptop before the push, and the pipeline's integration stage is green after the merge.
- [ ] A version was bumped: it is at least two weeks old and pinned exactly (ADR-0003), and the reason is in the pull request; Dependabot's own pull requests, held to the same age by their cooldown, count once CI is green on them.

## What is not done

A change with a known failing test, a skipped test without an issue, a `TODO` without an owner, or behaviour described only in the pull request and not in the repository.
