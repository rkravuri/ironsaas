# ADR-0022: The pipeline

Date: 2026-10-18
Status: Accepted

## Context

The S1 plan (3.11, T18): `ci.yml` on pull requests with build, checks, tests, shell checks, gitleaks and Trivy; `release.yml` on main with the images to GitHub Container Registry and an integration stage on the runner that provisions a tenant and runs the smoke. There is no Azure yet.

## Decision

1. **CI on every pull request and push to main**, five jobs that do not depend on each other: the .NET build with warnings as errors, the format check and every test (the integration tests start PostgreSQL, Azurite and OpenBao with Testcontainers on the runner's Docker); the shell's lint, type check, tests and build; the compose file validated with every profile; gitleaks over the whole history; Trivy over the dependency lock files for critical and high vulnerabilities, unfixed ones ignored.
2. **Release on main.** The five images (api, worker, control plane, fleet, shell) are built with Buildx and the GitHub Actions cache, pushed to `ghcr.io/<owner>/iron-*` tagged with the commit SHA and `main`, and each is scanned by Trivy for critical vulnerabilities.
3. **Integration on the runner, from the pushed images.** The job pulls the five images, then runs `infra/compose/scripts/ci-integration.sh`: the platform with the `ci` profile, the control plane from its images with the new `control` profile (the API on port 5200 and the fleet with the runner's Docker socket), the control database migrated and seeded with an operator and the smoke reader, and `Iron.Smoke` against tenant `acme`, which the fleet provisions from the same images. The report is uploaded whether the run passed or not; on failure the platform's, the control plane's and the units' logs are printed. A green run tags the images `integration`.
4. **The control plane runs as containers** when asked (`make up-control` on the laptop, the pipeline always). The `control` profile adds `control-api` and `fleet` to the platform compose, both reading their configuration from the control unit's secret written with the hosts the containers see (`postgres`, `blob`). The images the fleet starts units from come from `IMAGE_REPOSITORY` and `IMAGE_TAG`, which the pipeline sets to the registry path and the commit.
5. **Dependabot** proposes the version bumps for the actions, the .NET packages, the shell's packages and the base images, one grouped pull request per ecosystem per week; CI tells whether a bump is safe.
6. **A long-lived integration machine can come later** as a self-hosted runner that runs the same job; nothing in the workflow assumes the runner is ephemeral except the throw-away credentials `init-env.sh` creates.

## Consequences

- A merge to main is proven on a real stack before its images are called `integration`; nothing reaches that tag by hand.
- The runner's Docker runs the whole platform plus units, within the hosted runner's limits; the integration job takes several minutes and has a thirty-minute limit.
- The registry packages are private with the repository, which is why the integration job pulls before the fleet needs the images: the fleet carries no registry credentials, on purpose.
