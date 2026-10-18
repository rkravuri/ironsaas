# Tools

| Tool | What it does |
|---|---|
| `Iron.Smoke` | The smoke test (TF-240): drives the running stack from the outside through the control plane and the ingress, provisions or reuses the `smoke` tenant, signs in with a token and with a cookie, proves the denials, and finds the request's trace in Tempo by its correlation id. `make smoke` on the laptop; the integration stage in the pipeline (T18). Writes `smoke-report.json`. |

The scaffolding tool that creates a module or an entity from templates (TF-190) arrives after slice S1; the S1 modules are the templates it will copy.

Tools are console programs. They may reference the libraries they need and are outside the module boundary rules; they are not part of any image.
