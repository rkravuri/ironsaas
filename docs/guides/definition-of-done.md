# Definition of done

What a change needs before it is merged to main, which it reaches only through a pull request with every check green (ADR-0055). The pull request template summarises it; this page is the reference (TF-209).

## Every change

- [ ] `make verify` is green locally (it runs `make prd-check`, `make test` with coverage, `make shell-check`, `make shell-a11y`, the format check, `make security`, `make coverage` and `make trace` in order), and every check of the pull request is green; `make format` (`dotnet format` and Prettier, ADR-0040) makes no change.
- [ ] The security checks pass (ADR-0055): the security gate (dependencies and their licences, the images, misconfigurations and the repository's rules), every .NET security rule and ESLint's security plugin, and the coverage floors; a critical or high finding is fixed, or has an exception, never ignored. When `make coverage` says a floor may rise, the change raises it in `security/coverage.yaml`; a floor never falls.
- [ ] A task's report has its Risk section (the requirements it touched, their class in `docs/trace/risk.md`, what could go wrong and what proves it does not), committed as `docs/risk/<task>.md`, which the release's validation pack quotes (ADR-0052).
- [ ] New or changed behaviour has tests at the level that proves it: a unit test for a rule, an integration test on the container fixtures for anything that touches the database, the store or storage, a shell test for a page or a mapping.
- [ ] Every new test names the requirement criteria or the decision it proves (`[Proves("TF-033 AC2")]`, a title ending `[TF-033 AC2]`, ADR-0044), read against the criterion's text, and `make trace` passes: no unknown identifier, and no criterion of a done slice without a test or an approved exception.
- [ ] No warning was suppressed without a comment that says why.
- [ ] No secret, credential or personal data is in the change; gitleaks in CI is the net, not the first check.
- [ ] Documentation where behaviour changed: the README's first hour if the path changed, the developer guide if a way of doing things changed, the runbook if an operator's command or a symptom changed, the module's README if its responsibility changed.

## When it applies

- [ ] A requirement was added or changed: `make prd-csv` wrote its CSV and `make prd-check` passes; a new requirement takes the next free identifier in its area's block, and its dependencies point down the layers and along the process order (ADR-0041, ADR-0042).
- [ ] A decision was made (a library, a pattern, a boundary, a trade-off): its ADR is in `docs/adr/`, listed in the index, and named from the code or the guide that applies it.
- [ ] A module was added: the placement test is answered in its README, its projects declare `<IronLayer>` and live in their layer's folder, and its schema carries the layer prefix (ADR-0029).
- [ ] A module or an external interface was added (an anonymous endpoint, a public page, a host, a channel to another system): its threat model record is in `docs/security/threat-models/`, from the template, written and reviewed before it is built, and names it; the tests refuse a module or an anonymous endpoint without one (TF-077 AC2, ADR-0055).
- [ ] An exception was added, a finding in `security/exceptions.yaml` or a process exception in `docs/security/exceptions.md`: it has its reason, who approved it and when, and its expiry or re-check date, at most six months on (TF-076 AC2, ADR-0055).
- [ ] An entity changed: the migration is generated with `make ef-add`, read once, formatted with `make format`, committed with the entity, and the module's model test passes.
- [ ] An endpoint was added: it declares `RequirePermission`, `RequireSignedIn` or `AllowAnonymous`, and each success it answers with `.Produces<T>(status)` (TF-100 AC3); a new anonymous endpoint is a reviewed addition to the fixed list in `Iron.Host.Tests`. Every field of its body has a declared limit, rich text is a `RichText` with its maximum length, an export or a costly call names its rate limit (`.RateLimited`), and any SQL is a constant with typed parameters (TF-071, TF-072, ADR-0053).
- [ ] A high-risk action was added: it asks for a recent sign-in (`.RequireRecentAuthentication()`, or `IRecentAuthentication` in the service where the data decides), and is refused while impersonating (TF-248, ADR-0047, ADR-0049).
- [ ] A sensitive configuration save was added: it names its area and asks `IFourEyes` after its own checks, with an `IConfigurationChangeApplier` for its kind (TF-068, ADR-0052).
- [ ] A permission was added: it is in the module's Contracts, in the module's `Permissions`, and seeded into the system roles that should carry it.
- [ ] A licensed module or a feature was added: the module declares its licence and features (a kernel module declares none), the endpoints that need a feature say so, `make catalogue` was run and the catalogue committed (ADR-0034).
- [ ] A flag was added or retired: it is declared in the module's Contracts with its owner and purpose, a release or experiment flag with its removal date; a retired flag is marked so in the catalogue in the same change; `make catalogue` was run (ADR-0035). A kill switch is the kernel's only.
- [ ] A setting was added: it is declared in the module's Contracts with its scopes and rule, named in the shell's catalogue under `settings.names`, and `make catalogue` wrote the catalogue and `shell/src/api/setting-keys.ts` (ADR-0036).
- [ ] A duty was added: it is declared beside the module's permissions, listed in its `IModule`, and in the catalogue (ADR-0037).
- [ ] A business record type was added: its policy is registered with its display name (`AddRecordPolicy`), its lists and reads go through `IRecordPolicies`, and the module registers its records for merges (`AddUnitRecordOwners`) (ADR-0032, ADR-0038). It is tracked for field history, its sensitive and secret fields marked (ADR-0051); a field that must not be readable in a dump is an `EncryptedValue`, `[Searchable]` with its hash where it is found by its value (ADR-0054); its records are in a retention class (ADR-0050); and a document people sign declares its signable type and snapshot (ADR-0051).
- [ ] A test needs PostgreSQL, blob storage or the store: its fixture takes them from `tests/Iron.Testing`, in a database, containers and a mount of its own, and anything that lives in a whole server (a role, a container name, a policy) carries the project's name.
- [ ] A browser test makes data on the tenant: it makes it under the run's unit (`E2E_RUN_UNIT_ID`) and names it with the run's id, never under the root.
- [ ] Something a user does changed state: the audit event is written in the same transaction (ADR-0013), its name is a constant in the module's Contracts with its words beside it (ADR-0051), and a security alert rule that should count it names it, with its section in the incident runbook (ADR-0056).
- [ ] A shell route or page was added: its permission is declared in the manifest, its strings are in the catalogue, `make shell-check` passes, an accessibility spec walks it by keyboard in both schemes with its screenshots, and the brief's six questions (`docs/design/design-language.md` section 9) are answered in the pull request.
- [ ] A shell component, a token or a themed variant was added or changed: it has its example in the gallery (both schemes, both densities) and `make shell-a11y` passes.
- [ ] A drawing or pattern was added: it passes `docs/design/illustration-checklist.md` (one hand, original, accessible name or hidden, both schemes, reduced motion, patterns never behind text), answered line by line in the pull request.
- [ ] A host, an image, a provisioning step or the unit template changed: `make images`, `make update-unit TENANT=<slug>`, `make smoke TENANT=<slug>` and `make e2e TENANT=<slug>` passed on the laptop before the push, and the pipeline's integration stage is green after the merge.
- [ ] A version was bumped: it is at least two weeks old and pinned exactly (ADR-0003), and the reason is in the pull request; Dependabot's own pull requests, held to the same age by their cooldown, count once CI is green on them.

## What is not done

A change with a known failing test, a skipped test without an issue, a `TODO` without an owner, or behaviour described only in the pull request and not in the repository.
