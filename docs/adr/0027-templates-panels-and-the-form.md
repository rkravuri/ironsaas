# ADR-0027: Templates, panels and the form

Date: 2026-10-18
Status: Accepted

## Context

ADR-0024 (item 8) says products get the design language through the library, not by styling: page templates, panels and the form. TF-171 names them and asks that a server validation error appear on its field (AC3); TF-172 asks that the grid be operable by keyboard (AC2). D3 built them, and found that the shell's client read a validation problem in a shape the API never writes.

## Decision

1. **Five templates are the page skeletons.** `ListPage`, `RecordPage`, `WizardPage`, `ConsolePage` and `DashboardPage` in `shell/src/components/templates` open with the title in the display family and one line (the moment) and give the work its place: the grid filling the viewport with the density switch, the record header with its status as a word and the panels behind tabs, the steps with back and next and the summary beside them, the sections with a contents list, the cards with a big number. A product page composes a template; it does not lay itself out.
2. **The panels are the parts.** `RecordHeader`, `EmptyState` and `LoadingState` on the drawings, `ConfirmDialog` and `FormDialog` with one primary action, `AttachmentPanel` with download, upload and removal through callbacks (the page talks to its module's API and the object storage paths of ADR-0016), `HistoryPanel` on the audit events, and `ApprovalPanel` and `CommentPanel` as the shapes S5 and a later slice fill. A status is always a tone and a word, never a colour alone.
3. **The form is Mantine's with a Zod schema.** `useZodForm` wires a ten-line validator instead of a resolver package (ADR-0003 prefers fewer dependencies), validates a field after its first blur, and `Form` renders the fields, the top-of-form problem and the actions with the primary last and never two of them.
4. **Server problems map by one rule.** The client's problem schema now matches the API (`ProblemFactory`): `errors` is a list of `field`, `code` and `message`, the field a snake_case JSON path such as `items[0].unit_price`. Forms name their fields in camelCase, and `applyProblem` turns the API's path into the form's (`items.0.unitPrice`), puts each message on its field, and returns what found no field for the top of the form with the correlation id. A test holds a problem as `ValidationFilter` writes it.
5. **The grid selects and opens by keyboard.** Single-row selection: Space and Enter select the focused row, a double click or Ctrl+Enter (Cmd+Enter on a Mac) runs the row's action through `onRowOpen`, and `onSelectionChange` tells the page. The rule for the keys is a pure module. The Users page opens the user's details in a dialog on the record header until the API reads a user by id.
6. **Proof without the stack.** The browser tests that need no stack live beside the scan: the Users page runs against the preview with the API answered from memory (a signed-in administrator and twenty users), so the keyboard operation, the density switch and the page's accessibility are proven on every push; the same steps run against the smoke tenant in `e2e/` when the stack is up.

## Consequences

- A1 and A2 build their pages on the templates and the form; the first real form pages test the mapping against the real API.
- The client fix means every validation problem from the API now reaches the form; before, it parsed to a bare error.
- Saved views (S5), the behaviour behind the approval and comment panels, and attachment storage for a given module are later work; the shapes are fixed now so pages can plan for them.
- Options not taken: `mantine-form-zod-resolver` (a dependency for ten lines); react-hook-form (a second form library next to Mantine's); snake_case field names in forms (the API's names would leak into the pages); one generic page skeleton with options (five small templates read better than one configurable one).
