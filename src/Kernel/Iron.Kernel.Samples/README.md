# Iron.Kernel.Samples

Sample records for testing a running unit (ADR-0057). No product brings business records yet, so the smoke and the
browser tests meet the kernel's record machinery here: the record policy (ADR-0032), sharing (ADR-0038), visibility tags
(ADR-0057) and field history (ADR-0051), on a record page built from the shell's record template. A sample record has a
name and nothing else.

## Only where the operator marks the tenant

The API host loads the module only when the unit's configuration says `Samples:Enabled` (`UnitModules.ForUnit`). The
fleet writes it into the unit secret of a tenant the operator marked: `make provision TENANT=x SAMPLES=1`, or
`make sample-records TENANT=x` and then `make update-unit TENANT=x` for one that exists (`ON=0` unmarks it). Every other
unit never loads it, holds its permissions or has its schema (`kernel_samples`), and the licensing catalogue never names
it. The release's integration stage registers its tenant marked, and on the laptop echo and smoke are marked.

The installation record (ADR-0052) checks a unit whose database has the module's schema against the image's seeds with
the module's permissions (`sample_seeds_sha256` of the `configuration` command), and every other unit against those
without.

## What it serves

| Endpoint | Permission | What |
|---|---|---|
| `GET /api/v1/sample-records` | `samples.records.read` | The records the reader may see, through the policy |
| `POST /api/v1/sample-records` | `samples.records.manage` at the record's unit | A record at the working unit, or at the unit named |
| `GET /api/v1/sample-records/{id}` | `samples.records.read` at the record's unit, then the policy | One record |
| `PUT /api/v1/sample-records/{id}` | `samples.records.manage`, the policy's `update` | Rename it |

The record's sharing (`/api/v1/record-sharing/sample_record/{id}`) and tags (`/api/v1/record-tags/sample_record/{id}`)
are the kernel's own endpoints. The policy is `UnitVisibility` with `update`, `share` and `tag` for the manage permission.
