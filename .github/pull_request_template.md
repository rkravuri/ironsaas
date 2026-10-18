## What and why

## Definition of done (`docs/guides/definition-of-done.md`)

- [ ] `make test` and `make shell-check` are green locally; `make format` made no change
- [ ] New or changed behaviour has tests at the level that proves it
- [ ] If a decision was introduced, its ADR is in `docs/adr/` and listed in the index
- [ ] Documentation updated where behaviour changed (README, developer guide, runbook, module README)
- [ ] Endpoints declare a permission; entities have their generated migration; state changes write their audit event
- [ ] A host, image, step or template change was proven with `make images`, `make update-unit` and `make smoke`
- [ ] No secrets, credentials or personal data in the change
