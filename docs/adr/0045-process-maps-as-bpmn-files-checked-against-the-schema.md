# ADR-0045: Process maps as BPMN files, checked against the BPMN 2.0 schema

**Status:** Accepted
**Date:** 2026-10-21
**Decided by:** Product owner and architect

## Context

The product method asks every module for a process reference pack before its first slice, with process maps in BPMN (PM-001). The first pack, supplier management, came from the design session with its maps in Mermaid, which GitHub draws but no BPMN tool opens. Task SUP-gaps (`docs/prompts/SUP-gaps.md`) asks for the maps as BPMN 2.0 files beside the pack, each well-formed and valid against the BPMN 2.0 schema, and for that check in `make prd-check`. The requirements check is Python with the standard library only (ADR-0041, decision 8), and the standard library cannot validate a file against an XML schema. Nothing else on the laptop can either: `xmllint` is not installed and needs a system package, and the shell has no XML schema validator.

## Decision

1. **Where the maps live.** A pack is `docs/prd/suites/<suite>/process-reference/<module>.md`, named after its module's document. Its maps are BPMN 2.0 XML files in the folder of the same name beside it, one per map, for example `process-reference/supplier-management/main-flow.bpmn`. The Mermaid maps stay in the pack for reading on GitHub; the BPMN files are for BPMN tools and for the check.
2. **How a map is drawn.** One pool per map, a lane per role, the pack's steps as tasks named by their step identifier first (`SM-01 Request a new supplier`), exclusive gateways where the map branches, and a diagram (shapes and edges) for every element, so a tool opens the map as drawn. The maps are descriptive, not executable (`isExecutable="false"`).
3. **The check is a .NET tool, `tools/Iron.ProcessMaps`.** .NET's `XmlSchemaSet` validates against OMG's schema files with nothing to install beyond the SDK every developer already has. OMG's five schema files are kept unchanged in `tools/Iron.ProcessMaps/schema/`, with their licence in `docs/licences.md`. The tool checks, for every pack under `docs/prd`:
   - the pack has at least one map;
   - each map is well-formed and valid against `BPMN20.xsd` and the files it imports;
   - what the schema cannot see, because BPMN's references are mostly names, not XML identifiers: every reference names an element of the file, every flow node stands in exactly one lane, every flow node, lane, flow and annotation has its shape or edge on the diagram, and every task's name starts with its step identifier.
4. **When it runs.** `make prd-check` runs it after `tools/prd_check.py`, which holds a pack's section 2 to its module's requirements (PM-001 AC2). CI's job that runs `make prd-check` sets up the .NET SDK first. The tool's own tests are a test project, `tests/Iron.ProcessMaps.Tests`, and run with `make test`; one of them checks the packs' maps as committed.

## Options considered

- Python with the standard library, checking structure by hand. Rejected: it would be our reading of the schema, not the schema; the task asks for validity against the BPMN 2.0 schema.
- `xmllint` with the same schema files. Rejected: a system package that is not on the laptop and needs administrator rights to install; one more thing every developer and runner must have.
- A Python or npm package that validates schemas (`xmlschema`, `xmllint-wasm`). Rejected: a new dependency for one check, against ADR-0041's standard-library check and ADR-0003's two-week rule for a package nobody else needs.
- The check as a test in an existing test project. Rejected as the only form: the task puts it in `make prd-check`, where a documents change is checked without the full suite. The test project keeps the tool's own tests beside the others.

## Consequences

- Easier: any BPMN tool (Camunda Modeler, bpmn.io and others) opens a pack's maps; a map that the schema refuses, or whose reference, lane or shape is missing, stops `make prd-check` with the file and the line.
- Harder: `make prd-check` now needs the .NET SDK beside Python, and builds a small tool on its first run. A map is edited in a BPMN tool or as XML, and its Mermaid twin in the pack is kept in step by hand.
- Revisit: when a second pack arrives, whether a generator from one source should write both the Mermaid and the BPMN; if OMG publishes a later BPMN schema, whether to move to it.
