# Process exceptions

The controls of the secure development lifecycle that the project does not meet yet, each recorded with its reason,
who approved it and when, the date to check it again and the rule that ends it (TF-077, ADR-0055). A scanner's
finding that may wait is an exception in `security/exceptions.yaml`, which the build reads; this page holds what no
scanner reports. Each row is reviewed at its re-check date, and earlier when its ending rule is met.

| Id | The control | The exception | Why | Approved | Re-check | Ends |
|---|---|---|---|---|---|---|
| PE-1 | TF-077: every change passes code review by a second developer, and a pull request without one cannot merge (AC1). | Main's branch protection requires a pull request with every check green, but no approving review. The design session's review of each task, recorded in the task's report and pasted into its pull request as a comment before the merge, stands in for it. | The team has one developer. GitHub does not let the author of a pull request approve it, so a required approval would block every merge. Making the gap visible is the honest form of the control until a second developer can review. | 2026-10-24, by the product owner in the design session (the brief of S3 T10a) | 2027-04-24 | The day a second developer joins: main's protection is switched to one required approving review (`required_approving_review_count: 1` in `.github/branch-protection.json`, applied with `make protect-main`), and this row is closed with the date. |

## Closed

None yet.
