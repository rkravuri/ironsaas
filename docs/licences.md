# Licences of fonts, images and data

Everything that is not code and not written here by us, with its source and licence (ADR-0003, ADR-0024). Every addition is a line here in the same pull request.

| Material | Source | Licence | Used for |
|---|---|---|---|
| Source Sans 3 (variable) | `@fontsource-variable/source-sans-3` (Adobe Fonts, via Fontsource) | SIL Open Font License 1.1 | the reading family (D1) |
| Fraunces (variable) | `@fontsource-variable/fraunces` (Undercase Type, via Fontsource) | SIL Open Font License 1.1 | the display family (D1) |
| Tabler Icons | `@tabler/icons-react` | MIT | icons |
| The NCSC's 100,000 most used passwords: `100k-most-used-passwords-NCSC.txt` (99,840 lines, SHA-256 `c2e5696882c603b76bb67a47ee970897e5a76fc4c3f5547abe3d0ca340c576e0`), unchanged, as `src/Kernel/Iron.Kernel.Identity/Services/common-passwords.txt` | SecLists, `Passwords/Common-Credentials/`, commit `1a7bb9127eca9e6ff2fc0301c597fe6e16a0cb56` (2025-11-19), https://github.com/danielmiessler/SecLists, fetched 2026-10-21; the list is the UK National Cyber Security Centre's, whose own download links no longer answer | MIT (SecLists) | the common-password check of the password policy (TF-012, ADR-0046) |
| BPMN 2.0 XML schemas: `BPMN20.xsd`, `Semantic.xsd`, `BPMNDI.xsd`, `DI.xsd`, `DC.xsd` (version 20100501), unchanged, in `tools/Iron.ProcessMaps/schema/` | Object Management Group, https://www.omg.org/spec/BPMN/20100501/, fetched 2026-10-21 | The OMG specification licence of BPMN 2.0 (formal/2011-01-03): a royalty-free licence to use, copy and distribute the specification and to build software on it. The files carry no notice of their own; the owner confirms the reading | checking the process reference packs' BPMN maps (ADR-0045) |

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
