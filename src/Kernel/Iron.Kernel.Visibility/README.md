# Iron.Kernel.Visibility

Content visibility (TF-241, ADR-0057): visibility tags narrow what a person sees, after roles, permissions and units. Its
schema is `kernel_visibility`; the tags a record carries are a column of the record's own table, which the data library
filters by (`Iron.Kernel.Data.Visibility`).

## The six rules, and where each lives

| Rule | Where |
|---|---|
| 1. Administrators define the tags. | `TagService`: create, rename and describe, retire and reactivate, with `visibility.tags.manage`, tenant-wide. A code is capitals, digits, hyphens and underscores, at most 32; it never changes and is never used again. |
| 2. Records carry tags; a record without tags is visible to everyone who passes roles and units. | `BusinessRecord.VisibilityTags` and `VisibilityFilter` in the data library. |
| 3. People hold tags; a person who holds none sees every record. | `HoldingService` (`kernel_visibility.holdings`); people and service accounts alike. |
| 4. A person sees a tagged record when they share at least one tag with it. | `VisibilityFilter`, one more condition in the record policy's query, after the scope rule and the module's policy (ADR-0032). |
| 5. A person puts on a record only tags they hold. | `IRecordTags` in the data library: the policy action `tag` decides who may change a record's tags, and rule 5 which tags, putting on and taking off alike, unless the person holds none. |
| 6. Transactions may take their tags from their master. | `AddTagsFromMaster<T>(masterRecordType, masterId)` in the data library: each product declares it for its own record types. |

## What it serves

| Endpoint | Permission | What |
|---|---|---|
| `GET /api/v1/visibility-tags` | `visibility.tags.read` | The tags, with how many hold each |
| `POST /api/v1/visibility-tags`, `PUT .../{id}`, `POST .../{id}/retire`, `POST .../{id}/reactivate` | `visibility.tags.manage` | Define, rename, retire, reactivate |
| `GET /api/v1/visibility-tags/{id}/holders` | `visibility.tags.read` | Who holds a tag |
| `GET /api/v1/users/{id}/visibility-tags` | `visibility.tags.read` at the person's home unit | A person's tags, and those the reader may give |
| `PUT /api/v1/users/{id}/visibility-tags` | `visibility.assignments.manage` at the person's home unit | Give exactly these tags, under the escalation rule |
| `GET`, `PUT /api/v1/record-tags/{type}/{id}` | signed in; the record's policy decides | A record's tags, and those the reader may put on it (rule 5) |

The escalation rule (A2): a person gives, or takes away, only tags they hold, unless they hold none. A retired tag stays
on every record and person that carries it, so nothing becomes visible by surprise, and is given to nobody new.

## What it implements for others

- `IVisibilityTags` (the data library's): the codes a principal holds, retired ones included, or the delegator's while
  the principal acts for them (`IActingFor`, Authorization's), so the filter follows the grants (ADR-0037).
- `IVisibilityTagHolders` (its own contracts): who holds which tag, for access reviews, which list the tags each person
  holds and can review tag holders, a revoke removing the holding.

## History and audit

Every change of a tag is a `visibility.tag_*` event and a change of the tag's field history. A change of a person's tags
is one `visibility.holder_tags_changed` event on the person's own record (`principal`), with the field `visibility_tags`
before and after, so the user record's History shows it, and a line of their security activity (Identity's
`ISecurityActivity`). A record's tags are a field of the record, so its own history records them.

## Tests

`tests/Kernel/Iron.Kernel.Visibility.Tests` (needs Docker): a tag's life, the escalation rule, the audit of a holder's
change, a service account's tags, the delegator's tags while acting. The filter, rule 5 and rule 6 are proven in
`tests/Kernel/Iron.Kernel.Data.Tests/Policies/VisibilityTagsTests.cs` on the data tests' sample module.
