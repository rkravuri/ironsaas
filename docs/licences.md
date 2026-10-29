# Licences of fonts, images and data

Everything that is not code and not written here by us, with its source and licence (ADR-0003, ADR-0024). Every addition is a line here in the same pull request. A NuGet package carries its licence beside its version in `Directory.Packages.props`, and an image of the laptop stack beside its tag in `infra/compose/compose.yaml`; the ones a task asked to record are below as well.

| Material | Source | Licence | Used for |
|---|---|---|---|
| Source Sans 3 (variable) | `@fontsource-variable/source-sans-3` (Adobe Fonts, via Fontsource) | SIL Open Font License 1.1 | the reading family (D1) |
| Fraunces (variable) | `@fontsource-variable/fraunces` (Undercase Type, via Fontsource) | SIL Open Font License 1.1 | the display family (D1) |
| Tabler Icons | `@tabler/icons-react` | MIT | icons |
| The NCSC's 100,000 most used passwords: `100k-most-used-passwords-NCSC.txt` (99,840 lines, SHA-256 `c2e5696882c603b76bb67a47ee970897e5a76fc4c3f5547abe3d0ca340c576e0`), unchanged, as `src/Kernel/Iron.Kernel.Identity/Services/common-passwords.txt` | SecLists, `Passwords/Common-Credentials/`, commit `1a7bb9127eca9e6ff2fc0301c597fe6e16a0cb56` (2025-11-19), https://github.com/danielmiessler/SecLists, fetched 2026-10-21; the list is the UK National Cyber Security Centre's, whose own download links no longer answer | MIT (SecLists) | the common-password check of the password policy (TF-012, ADR-0046) |
| BPMN 2.0 XML schemas: `BPMN20.xsd`, `Semantic.xsd`, `BPMNDI.xsd`, `DI.xsd`, `DC.xsd` (version 20100501), unchanged, in `tools/Iron.ProcessMaps/schema/` | Object Management Group, https://www.omg.org/spec/BPMN/20100501/, fetched 2026-10-21 | The OMG specification licence of BPMN 2.0 (formal/2011-01-03): a royalty-free licence to use, copy and distribute the specification and to build software on it. The files carry no notice of their own; the owner confirms the reading | checking the process reference packs' BPMN maps (ADR-0045) |
| DB-IP IP to City Lite, month 2026-10: `dbip-city-lite-2026-10.mmdb.gz` (SHA-256 `c839d407f85a1e0bddba354cef16b2c398182d1c34ed8d985555dce134b36b14`), unpacked unchanged into the API image at `/app/locations/dbip-city-lite.mmdb` by the `locations` stage of `src/Hosts/Iron.Host.Api/Dockerfile`; not in the repository | DB-IP, https://db-ip.com/db/download/ip-to-city-lite, fetched at image build from the pinned month and checked against the pinned sum | Creative Commons Attribution 4.0 International; the attribution "IP Geolocation by DB-IP" with a link to https://db-ip.com shows under every list of session places | the approximate city and country of a session, from its network address (TF-022 AC4, S3 T5, ADR-0049) |

Drawings and patterns under `shell/src/design/` are original work of this repository.

## Packages a task asked to record

NuGet packages carry their licence in a comment in `Directory.Packages.props` (ADR-0003). The shell's `package.json` cannot carry comments, so a frontend package whose licence a task asks to record is listed here.

| Package | Version | Licence | Used for |
|---|---|---|---|
| `@openfeature/web-sdk` | 1.10.0 (2026-07-28) | Apache-2.0 | evaluating flags in the shell through OpenFeature (A5, ADR-0035) |
| `@openfeature/core` | 1.12.0 (2026-07-28) | Apache-2.0 | the web SDK's shared types, a peer dependency it needs (A5) |
| `OpenFeature` (NuGet, recorded here too) | 2.14.1 (2026-08-10) | Apache-2.0 | evaluating flags in the API (A5) |
| `@mantine/spotlight` | 9.7.3 | MIT | the search palette, built from its parts as a combobox (D4, S2-close) |
| `prettier` | 3.9.9 (2026-09-23) | MIT | formatting the shell's code, a dev dependency (S2-close, ADR-0040) |
| `qrcode-generator` | 2.0.4 (2025-08-07) | MIT (Kazuhiko Arase) | drawing an authenticator app's QR code at enrolment, as one SVG path (S3 T3, ADR-0047); no dependencies |
| `Fido2` and `Fido2.Models` (NuGet, recorded here too) | 4.2.0 (2026-10-02) | MIT (Fido2NetLib) | WebAuthn registration and assertion for passkeys on the server (S3 T3, ADR-0047) |
| `NSec.Cryptography` (NuGet, Fido2's dependency) | 25.4.0 | MIT | Ed25519 keys of passkeys that use them, for Fido2 |
| `libsodium` (NuGet, NSec's dependency) | 1.0.20.1 | ISC | the native library under NSec |
| `System.Formats.Cbor` (NuGet, Fido2's dependency) | 9.0.0 | MIT (.NET Foundation) | reading WebAuthn's CBOR for Fido2; the Identity tests' software authenticator writes it |
| `MaxMind.Db` (NuGet, recorded here too) | 5.2.0 (2026-09-10) | Apache-2.0 (MaxMind) | reading the IP-to-city database in MMDB form for a session's place (S3 T5, ADR-0049); it reads DB-IP's file, not MaxMind's data |
| `eslint-plugin-security` | 4.2.0 (2026-10-01) | Apache-2.0 (eslint-community) | the shell's security lint rules, a dev dependency (S3 T10a, ADR-0055) |
| `@vitest/coverage-v8` | 5.0.5 (2026-10-15) | MIT (Vitest) | the shell's line coverage for its floor, a dev dependency; the one version Vitest 5.0.5 accepts, two weeks old on 2026-10-29 (S3 T10a, ADR-0055) |
| `coverlet.MTP` (NuGet, recorded here too) | 10.1.0 (2026-09-27) | MIT (coverlet-coverage) | the .NET tests' line coverage for its floor, in every test project (S3 T10a, ADR-0055) |

## Images S3 added to the laptop stack

Each pinned in `infra/compose/compose.yaml` with its licence beside it, run on the laptop and in the pipeline's integration stage, never in a tenant's unit.

| Image | Version | Licence | Used for |
|---|---|---|---|
| `ghcr.io/navikt/mock-oauth2-server` | 6.0.4 | MIT (NAV) | the single sign-on test provider at `idp.localhost`, profile `sso`, for the smoke and the browser tests (S3 T4, ADR-0048) |
| `prom/alertmanager` | v0.34.1 | Apache-2.0 (Prometheus) | the operator's alerting on the laptop, profiles `telemetry` and `ci`, paging nobody (S3 T10b, ADR-0056) |

## Tools the pipeline runs

Run from their official images or actions at a pinned version, the same on the laptop and in CI, never shipped in an image (S3 T10a, ADR-0055). The rules the rule-based scanner applies are the repository's own, in `security/rules/`.

| Tool | Version | Licence | Used for |
|---|---|---|---|
| Trivy (`ghcr.io/aquasecurity/trivy`) | 0.76.0 (2026-10-14) | Apache-2.0 (Aqua Security) | the dependencies, their licences and the misconfigurations (`make security`), the images and their CycloneDX bills of materials (`make image-scan`); its vulnerability database is GitHub's advisories and the distributions' own, downloaded at each run |
| Semgrep Community Edition (`semgrep/semgrep`) | 1.180.0 (2026-10-07) | LGPL-2.1 (Semgrep) | the repository's own rules for C# and TypeScript (`make security`); run with metrics off and no registry rule |
| gitleaks (`ghcr.io/gitleaks/gitleaks`) | 8.30.1 | MIT | the secret scan of the history, and its test (S3 T9b); CI runs it through `gitleaks/gitleaks-action`, whose own terms are free for a personal account's repositories and ask an organisation for a licence key, to be read again if the repository moves to one |
