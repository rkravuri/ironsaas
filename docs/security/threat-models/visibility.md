# Threat model: content visibility

- **Modules:** visibility
- **Endpoints:** none
- **Reviewed:** 2026-10-29 (V1)

## What it protects

Which records a person sees beyond what their roles and units allow (TF-241, ADR-0057): the tenant's visibility tags,
who holds them, and the tags every business record carries in its own table. A person who holds tags sees a tagged
record only when they share one of its tags; a mistake in the tags shows a region's or a project's records to people
outside it, or hides them from the people who need them.

## Who can reach it

Signed-in people and service accounts. Administrators with `visibility.tags.manage` define the tags, tenant-wide; those
with `visibility.assignments.manage` at a person's home unit give the person tags; a person whose record's policy allows
the action `tag` changes that record's tags. Every list and read by id of a business record runs the tag filter for
the reader, in the database.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | Seeing records as another person's tags. | The filter takes the request's principal, or the delegator while the principal acts for one, as the grants do; a token is its service account, which holds tags of its own. | TF-241, TF-037 |
| Tampering | Putting a tag one does not hold on a record, or taking one off, to move it out of others' sight or into one's own. | Rule 5: a person puts on or takes off only tags they hold, unless they hold none; the record's policy decides who may change tags at all; a retired tag is put on nothing. | TF-241 AC5 |
| Repudiation | Denying a change of a tag, a person's tags or a record's tags. | Every change is an audit event in the chain, with the tags before and after: the tag's, on the person's record, and in the record's field history. | TF-241 AC7, TF-062 |
| Information disclosure | A tagged record shown to someone outside its tags: in a list, a total, by id, through a share or through the working unit. | The filter is one more condition in the policy's query, after the scope rule and the module's policy, so it only narrows and the totals count what the reader sees; a record outside the reader's tags answers not found, as one outside their units does; a retired tag keeps filtering. | TF-241 AC1 to AC4, AC6, TF-035 |
| Denial of service | Hiding records from the people who need them by tagging them. | Only a person who holds a tag can put it on a record; the explanation names the tag check, so an administrator sees why a record is hidden; a person who holds no tags sees every record. | TF-241 AC2, TF-039 |
| Elevation of privilege | Giving oneself or someone else a tag one does not hold, to see more. | The escalation rule: a person gives and takes away only tags they hold, unless they hold none; giving tags needs `visibility.assignments.manage` at the person's home unit; access reviews list who holds which tags and can take them away. | TF-241 AC7, TF-038 |

## What remains

- A person who holds no tags sees every record their roles and units allow, by the owner's rule 3: giving someone their
  first tag narrows what they see, and taking their last one away widens it. The access reviews list the tags each
  person holds so that a reviewer sees it.
- Approvals, tasks and notifications that respect the tags are TF-243, for the workflow and notification slices.
