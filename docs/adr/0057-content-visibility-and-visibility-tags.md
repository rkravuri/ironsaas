# ADR-0057: Content visibility: visibility tags in the record policy's query, stored on the record, measured first

**Status:** Proposed
**Date:** 2026-10-29
**Decided by:** Product owner and architect, in the brief of V1 and in its session, where the owner chose sample records in units marked for testing

## Context

TF-241 narrows what a person sees by visibility tags, after roles, permissions and units, by six rules the owner
decided: administrators define the tags; master data and transactions carry them, and a record without tags is
visible to everyone who passes roles and units; people hold tags, and a person who holds none sees every record; a
person sees a tagged record when they share at least one tag with it; a person puts on a record only tags they hold;
and transactions may take their tags from their master, as each product says. It must exist before the first business
record type, so every business module has tags from its first day.

What a reader may see is already decided in one place: the scope rule for the record type's read permission with the
sharing mode, joined with the module's policy (ADR-0030, ADR-0032, ADR-0038). Every list and read by id of a business
record goes through `IRecordPolicies`; the data library references no module and declares what it needs of others.

## Decision

1. **A kernel module, `Iron.Kernel.Visibility`** (`kernel_visibility`), keeps the tenant's tags and who holds them,
   with three permissions: `visibility.tags.read`, `visibility.tags.manage` (tenant-wide) and
   `visibility.assignments.manage` (at a person's home unit). A tag has a code, a name, a description and a status. Its
   code is capitals, digits, hyphens and underscores, at most 32; it never changes and is never used again, because
   records and people carry it.
2. **The filter is one more condition in the policy's query**, after the scope rule with the sharing mode and after the
   module's policy, joined with AND, for lists and reads by id alike (`PolicyEvaluation.ReadableAsync`). A reader who
   holds tags sees a record without tags or one that carries at least one of theirs; one who holds none is not
   filtered. So it only narrows, never widens a share or the working unit, and a list's total counts what the reader
   sees. A record outside the reader's tags answers not found, as one outside their units does (ADR-0032). The data
   library declares `IVisibilityTags` (what a principal holds, the tags that exist); the module implements it.
3. **Storage: a column of tag codes on each module's table, not one kernel table.** The brief asked for one kernel
   table joined in the query, as the shares are, unless a column measured clearly faster.
   `tools/measurements/visibility-tags-storage.sh` measured both on PostgreSQL 19rc1, the stack's: 120,000 records in
   20 units, a second module's records' tags beside them in the kernel table (234,641 rows), twelve tags, 29.9 % of
   records untagged, the rest with one, two or three; the queries the policy builds, for a reader of every unit holding
   one tag and one of five units holding two; medians of 40 runs (200 for a read by id).

   | Query | Kernel table | Column | No tag filter |
   |---|---|---|---|
   | List page of 50 | 19.0 and 47.4 ms | 0.14 and 0.38 ms | 0.15 and 0.22 ms |
   | The list's total | 39.8 and 72.1 ms | 15.2 and 22.5 ms | 7.7 and 7.9 ms |
   | Read by id | 0.18 and 0.11 ms | 0.07 and 0.08 ms | 0.13 and 0.10 ms |

   The kernel table's "no tags, or a shared one" is two subqueries joined by OR, which the planner runs as hashed
   subplans over every tag row of the record type before the page's first row, so a page costs as much as the type has
   tags; the column is two conditions on the row, and the page walks the sort's index and stops at 50. A list request
   (a page and its total) is three to four times faster with the column. So `BusinessRecord.VisibilityTags` is a
   `text[]` of codes on every business record's table, empty by default, so a table that had records before it
   carried tags migrates without a step. No index serves the filter, which is a condition on the row; none is made.
4. **A retired tag stays where it is** and still filters: records and people keep it, so a retirement never makes a
   record visible to someone who could not see it. It is put on nothing new and given to nobody; it can be taken off.
5. **Rule 5, and the escalation rule for people.** Changing a record's tags is the policy action `tag`, which the
   module's policy decides (usually its manage permission); `IRecordTags` then lets a person put on, and take off,
   only tags they hold, unless they hold none; a refusal names the tag. Taking off is held to the same rule as putting
   on: a person who could strip a tag they do not hold could take a record out of other people's sight, or widen it to
   everyone by stripping the last one. Giving a person tags follows A2's escalation rule the same way: one gives, and
   takes away, only tags one holds, unless one holds none.
6. **Rule 6 is an extension point**: `AddTagsFromMaster<T>(masterRecordType, masterId)`. A new record of `T` without
   tags of its own takes, when it is saved, the tags its master carries then; later changes of the master's tags do not
   follow. The master is named by its record type as its policy registers it, so a product never references another
   module's entity. The kernel declares none; each product says which of its record types take their master's tags,
   in its own PRD.
7. **While acting for someone, the tags are the delegator's**, as the grants are (ADR-0037, ADR-0038: the person works
   with the delegator's rights in full), through Authorization's `IActingFor`, which also follows an explanation's
   assumed acting.
8. **The explanation names the tag check** (ADR-0039) as its sixth check, after the record's policy: the record's tags,
   the person's, the ones they share, in words, from the policy engine's own decision. When the tags fail, the record
   is not found whatever the action, as the query answers; the policy's check then passes as far as seeing the record.
9. **Access reviews.** Every item keeps the tags its person held when the campaign started (TF-241 AC7). A campaign's
   subject is role assignments, as before, or visibility tag holdings, each at its person's home unit; confirming
   keeps a holding, revoking takes the tag away at once through the module, in the decision's transaction. A role
   item's signed snapshot keeps its form, so signatures made before still verify.
10. **History and audit.** A tag is tracked, and each change is a `visibility.tag_*` event too. A change of a person's
    tags is one `visibility.holder_tags_changed` event on the person's own record (`principal`), with the field
    `visibility_tags` before and after, so the user record's History shows it, and a line of their security activity
    (Identity's `ISecurityActivity`, which writes the row only). A record's tags are a field of the record, so its own
    history records them, as every other field.
11. **Sample records, in units marked for testing.** No product has records yet, and the smoke and the browser tests
    must tag one in a running unit. At the owner's choice, the API image carries `Iron.Kernel.Samples`, one record type
    with the kernel's policy blocks, loaded only where the unit's configuration says `Samples:Enabled`, which the fleet
    writes into the unit secret of a tenant the operator marked (audited). Every other unit never loads it, holds its
    permissions or has its schema; the licensing catalogue never names it; the installation record checks a marked
    unit against the seeds with its permissions, which the image states beside the others.

## Options considered

- One kernel table of record tags, joined as the shares are. Measured and rejected: see 3.
- A tag column without the codes, holding the tags' ids. Rejected: the history and every export would show ids; a code
  is the tag's for good, so the record keeps it as it is read.
- Letting anyone who may change a record's tags take off any tag. Rejected: see 5.
- The person's own tags while acting for someone. Rejected: the delegate could not do the delegator's work on records
  the delegator sees and they do not; a delegation is the delegator's rights, which the grants already follow.
- A retired tag dropped from the filter. Rejected: retiring a tag would make its records visible to everyone holding
  other tags at once.
- Tag reviews in the Visibility module, with campaigns of their own. Rejected: reviewers, the owner's routing, the
  escalation, signatures and progress would all be built twice; the review's subject is enough.
- No sample records in the product, the smoke and the browser tests proving tags on people only. Offered in the
  session; the owner chose the gated module, so the record template's control is proven against a running unit.

## Consequences

- Easier: every business record type has tags from its first day, with no code of its own: a column the conventions
  map, a filter its policy applies, a control on the record template (`tags` on `RecordPage`), the field in its
  history. A product says only which of its record types take their master's tags.
- Harder: every business record table carries a `visibility_tags` column. A product that wants records found by a tag
  ("every record tagged APAC") adds a GIN index on it then; no query needs one now. Approvals, tasks and notifications
  that respect the tags are TF-243, for the workflow and notification slices.
- A person who holds no tags sees every record their roles allow, by rule 3: giving someone their first tag narrows
  what they see, and taking their last one away widens it. The reviews list the tags each person holds for this reason.
- Revisit when a product needs tags on records of another kind than business records, or a tag hierarchy.
