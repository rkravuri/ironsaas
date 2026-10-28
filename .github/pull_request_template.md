## What and why

The task's prompt section (`docs/prompts/<slice>.md`), its report below, and its risk (`docs/risk/<task>.md`).

## Review

The design session's review stands in for a second developer's until one joins (PE-1, `docs/security/exceptions.md`):
its acceptance message is pasted here as a comment before the merge, which is a merge commit (ADR-0055).

## Definition of done (`docs/guides/definition-of-done.md`)

- [ ] `make verify` is green locally and every check here is green; `make format` made no change
- [ ] New or changed behaviour has tests at the level that proves it, each naming what it proves
- [ ] The security checks pass; a critical or high finding is fixed or has an exception with its expiry
- [ ] A new module or external interface has its threat model in `docs/security/threat-models/`
- [ ] If a decision was introduced, its ADR is in `docs/adr/` and listed in the index
- [ ] Documentation updated where behaviour changed (README, developer guide, runbook, module README)
- [ ] Endpoints declare a permission; entities have their generated migration; state changes write their audit event
- [ ] A host, image, step or template change was proven with `make images`, `make update-unit` and `make smoke`
- [ ] No secrets, credentials or personal data in the change
