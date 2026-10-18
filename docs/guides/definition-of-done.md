# Definition of done

What a change needs before it is merged to main. The pull request template summarises it; this page is the reference (TF-209).

## Every change

- [ ] `make test` is green locally and in CI; `make format` makes no change.
- [ ] New or changed behaviour has tests at the level that proves it: a unit test for a rule, an integration test on the container fixtures for anything that touches the database, the store or storage, a shell test for a page or a mapping.
- [ ] No warning was suppressed without a comment that says why.
- [ ] No secret, credential or personal data is in the change; gitleaks in CI is the net, not the first check.
- [ ] Documentation where behaviour changed: the README's first hour if the path changed, the developer guide if a way of doing things changed, the runbook if an operator's command or a symptom changed, the module's README if its responsibility changed.

## When it applies

- [ ] A decision was made (a library, a pattern, a boundary, a trade-off): its ADR is in `docs/adr/`, listed in the index, and named from the code or the guide that applies it.
- [ ] An entity changed: the migration is generated with `make ef-add`, read once, committed with the entity, and the module's model test passes.
- [ ] An endpoint was added: it declares `RequirePermission`, `RequireSignedIn` or `AllowAnonymous`; a new anonymous endpoint is a reviewed addition to the fixed list in `Iron.Host.Tests`.
- [ ] A permission was added: it is in the module's Contracts, in the module's `Permissions`, and seeded into the system roles that should carry it.
- [ ] Something a user does changed state: the audit event is written in the same transaction (ADR-0013) and its name is a constant in the module's Contracts.
- [ ] A shell route or page was added: its permission is declared in the manifest, its strings are in the catalogue, `make shell-check` passes, and the brief's six questions (`docs/design/design-language.md` section 9) are answered in the pull request.
- [ ] A drawing or pattern was added: it passes the illustration checklist (original, one hand, accessible name or hidden, both schemes, reduced motion).
- [ ] A host, an image, a provisioning step or the unit template changed: `make images`, `make update-unit TENANT=<slug>` and `make smoke` passed on the laptop before the push, and the pipeline's integration stage is green after the merge.
- [ ] A version was bumped: the reason is in the pull request; Dependabot's own pull requests count once CI is green on them.

## What is not done

A change with a known failing test, a skipped test without an issue, a `TODO` without an owner, or behaviour described only in the pull request and not in the repository.
