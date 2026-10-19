# Foundation shell

One React build that serves every tenant hostname (ADR-0020). Vite, React 19.3, TypeScript, Mantine 9.7.3, TanStack Query, react-i18next, Zod.

| Folder | Holds |
|---|---|
| `src/api/` | `client.ts` (the one way to call the API: problems become `ApiError` with the correlation id), `identity.ts` (schemas and calls of the Identity module) |
| `src/auth/` | the session provider (`/me`), the login page, the invitation page |
| `src/modules/` | `registry.ts` (manifests, permission filtering, strict validation), `index.ts` (the one list, and the one file that imports every layer), and a folder per layer, `kernel/`, `business/` and `products/` (ADR-0029), with one folder per module and its manifest and pages: `kernel/identity/` today |
| `src/shell/` | the layout (header, navigation, sign out), the guards, the error boundary |
| `src/design/` | the design foundation (ADR-0025): `tokens.ts` (the one place a colour, a size, a family or a duration is written; typed, and emitted as `--iron-*` CSS variables through the theme), `colour.ts` (the OKLCH scale generator), `contrast.ts` (WCAG contrast), `density.ts` (comfortable or compact), `motion.ts` (the reduced-motion hook), `global.css` (the reduced-motion rule); `patterns/` (the seeded mandala generator, the tenant mark, the hatch, the draw-in); `illustrations/` (the eleven drawings in one hand behind the `Drawing` frame, reviewed against `docs/design/illustration-checklist.md`); the only folder where a colour value is written |
| `src/components/data-grid/` | `DataGrid` (AG Grid Community, infinite row model, single-row selection by keyboard, the row's action on Ctrl+Enter or a double click), `list-query.ts` (the ListQuery mapping), `keys.ts` (the keyboard rule), the empty-state overlay, the Mantine-variable theme; the only folder that imports ag-grid (ADR-0021) |
| `src/components/templates/` | the five page templates (ADR-0027): `ListPage`, `RecordPage`, `WizardPage`, `ConsolePage`, `DashboardPage`, each opening with the title in the display family and one line |
| `src/components/panels/` | the panels: `RecordHeader`, `EmptyState`, `LoadingState`, `ConfirmDialog`, `FormDialog`, `AttachmentPanel`, `HistoryPanel`, `ApprovalPanel`, `CommentPanel`; `DensitySwitch` sits beside them |
| `src/components/form/` | `Form` on Mantine's form with a Zod schema (`useZodForm`), inline validation after the first blur, one primary action, and `applyProblem`, which puts the API's field problems on their fields |
| `src/i18n/` | the English catalogue; every string goes through it |
| `src/gallery/` | the gallery at `/gallery`, development builds only: every token and every themed component in both schemes and both densities; what the accessibility scan reads |
| `src/test/` | the test setup and the render and fetch helpers |
| `e2e/` | the Playwright tests against the smoke tenant (`make e2e`), and under `e2e/a11y/` the browser tests that need no stack: the axe scan of the gallery and of the login pages, and the Users page by keyboard with the API answered from memory (`npm run a11y`, its own `playwright.a11y.config.ts`) |
| `public/` | `scheme.js`, the external script that sets the colour scheme before the first paint; the content security policy forbids an inline one |

Commands: `npm run dev` (Vite on :5173, API paths proxied to `IRON_API_URL`, default http://localhost:5100), `npm run check` (lint, tests, build), `npm run build`, `npm run a11y` (builds the gallery with `vite build --mode gallery` into `dist-gallery`, serves it with `vite preview` on :4180 and runs axe on it in both schemes and both densities, leaving a screenshot of each in `test-results/`; browsers once with `npm run e2e:install`). The theme is `src/theme.ts`, built from `src/design/tokens.ts`.

Adding a module: put it under its layer (`src/modules/kernel/<name>/`, `business/` or `products/`, the same placement as its .NET module), export a `ModuleManifest` and add it to `src/modules/index.ts`. A module imports from its own layer or a lower one only; ESLint refuses a kernel module that imports `modules/business` or `modules/products`, and a business module that imports `modules/products`. Every route and navigation entry declares its permission (`null` for any signed-in principal); a missing one throws in development.

The grid rule: nothing imports `ag-grid-*` except `src/components/data-grid/` (ESLint enforces it; `src/lint.test.ts` proves it). A page gets a server-driven grid by declaring columns and a `fetchPage` that takes the ListQuery string.

The colour rule: no hex, `rgb()`, `hsl()`, CSS colour name or Mantine palette name in a `style`, `styles`, `vars` or `gradient` object or in `c`, `bg`, `bd`, `color`, `fill` or `stroke`, anywhere under `src/` except `src/design/` and the grid theme (ESLint enforces it; `src/lint.test.ts` proves it and reads the `.css` files under `src/` with the same rule). Name a meaning instead: `color="danger"`, `c="dimmed"`, `var(--iron-ink)`. A title of a moment takes the display family with `<Title order={2} variant="display">`; the work area keeps the reading family.
