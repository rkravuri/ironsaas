# ADR-0021: DataGrid over AG Grid Community

Date: 2026-10-18
Status: Accepted

## Context

The S1 plan (3.10): `DataGrid` wraps AG Grid Community, maps its paging, sorting and filter state to the ListQuery grammar (TF-101, ADR-0009), reads the standard envelope, and nothing else imports AG Grid, so Enterprise can be switched on by licence in one place. AG Grid Community 36.2.0 is current.

## Decision

1. **The infinite row model.** Paging, sorting and filtering are the grid's own controls, and each change makes the grid ask the wrapper's datasource for a block. The datasource turns the block (`startRow`, `endRow`), the sort model and the filter model into one ListQuery string and answers with the envelope's `data` and `total_count`. No client-side sorting or filtering anywhere: what the user sees is what the API ordered and filtered.
2. **The mapping is a pure module.** `list-query.ts` builds `page_number`, `page_size` (capped at the API's 200), `sort` (minus for descending) and one `filter=field:operator:value` per term. Text filters map to `contains`, `startswith`, `eq` and `ne`; number and date filters to the ordered operators. A filter model the grammar cannot express is dropped, never guessed. Columns declare which filter the API allows (`text`, `number`, `date` or none) and whether they sort, from the API's field table.
3. **Problems are shown, not swallowed.** A failed page becomes the API's problem message with its correlation id above the grid, with a retry that purges the grid's cache. A capped page size is reported as a notice.
4. **Theming through Mantine's variables.** The Theming API's parameters are set to Mantine CSS variables, so the grid follows the theme and the colour scheme with no second set of tokens.
5. **Only the modules in use are registered** (infinite row model, pagination, the three filters, the column API; the validation module in development), and the grid is a lazy chunk: the login page and the shell do not pay for it.
6. **The lint rule is the boundary.** ESLint refuses `ag-grid-*` imports outside `src/components/data-grid`, and a test runs ESLint to prove it. The column chooser is the wrapper's own (Community has no columns panel); on Enterprise it would become the tool panel inside this one folder.

## Consequences

- A module gets a server-driven grid by declaring columns and a `fetchPage`; the Users page is twenty lines.
- Set filters and the columns panel are Enterprise; the wrapper keeps simple text filters and its own chooser until a licence changes that.
- Row selection, editing and exports are not in S1; they land in the wrapper when a product needs them.
