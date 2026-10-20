# Tools

| Tool | What it does |
|---|---|
| `Iron.Smoke` | The smoke test (TF-240): drives the running stack from the outside through the control plane and the ingress, provisions or reuses the `smoke` tenant, signs in with a token and with a cookie, proves the denials, and finds the request's trace in Tempo by its correlation id. `make smoke` on the laptop; the integration stage in the pipeline (T18). Writes `smoke-report.json`. |

| `prd_check.py` | The requirements check (ADR-0041): every identifier once across the documents in `docs/prd` and their CSVs, every identifier of the archived Functional Foundation PRD in the map, criteria in the Given, When, Then form with an error case, the product PRDs' sections, and each CSV equal to its markdown. `make prd-check` runs its tests (`prd_check_test.py`) and then the check, in CI too; `make prd-csv` writes the CSVs from the markdown. |

The scaffolding tool that creates a module or an entity from templates (TF-190) arrives after slice S1; the S1 modules are the templates it will copy.

Tools are console programs with `<IronLayer>Tool</IronLayer>` (ADR-0029). They may reference the libraries they need and are outside the module boundary rules; they are not part of any image. The requirements check is a Python script with the standard library only, outside the .NET build.
