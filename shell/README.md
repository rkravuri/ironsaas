# Foundation shell

One React build that serves every tenant hostname (ADR-0020). Vite, React 19.3, TypeScript, Mantine 9.7.3, TanStack Query, react-i18next, Zod.

| Folder | Holds |
|---|---|
| `src/api/` | `client.ts` (the one way to call the API: problems become `ApiError` with the correlation id), `identity.ts` (schemas and calls of the Identity module) |
| `src/auth/` | the session provider (`/me`), the login page, the invitation page |
| `src/modules/` | `registry.ts` (manifests, permission filtering, strict validation), `index.ts` (the list), one folder per module with its manifest and pages |
| `src/shell/` | the layout (header, navigation, sign out), the guards, the error boundary |
| `src/design/` | the design foundation (ADR-0025): `tokens.ts` (the one place a colour, a size, a family or a duration is written; typed, and emitted as `--iron-*` CSS variables through the theme), `colour.ts` (the OKLCH scale generator), `contrast.ts` (WCAG contrast), `density.ts` (comfortable or compact), `motion.ts` (the reduced-motion hook), `global.css` (the reduced-motion rule); the only folder where a colour value is written |
| `src/components/data-grid/` | `DataGrid` (AG Grid Community, infinite row model), `list-query.ts` (the ListQuery mapping), the Mantine-variable theme; the only folder that imports ag-grid (ADR-0021) |
| `src/i18n/` | the English catalogue; every string goes through it |
| `src/gallery/` | the gallery at `/gallery`, development builds only: every token and every themed component in both schemes and both densities; what the accessibility scan reads |
| `src/test/` | the test setup and the render and fetch helpers |
| `e2e/` | the Playwright tests against the smoke tenant (`make e2e`), and under `e2e/a11y/` the axe scan of the gallery (`npm run a11y`, its own `playwright.a11y.config.ts`, no stack needed) |
| `public/` | `scheme.js`, the external script that sets the colour scheme before the first paint; the content security policy forbids an inline one |

Commands: `npm run dev` (Vite on :5173, API paths proxied to `IRON_API_URL`, default http://localhost:5100), `npm run check` (lint, tests, build), `npm run build`, `npm run a11y` (builds the gallery with `vite build --mode gallery` into `dist-gallery`, serves it with `vite preview` on :4180 and runs axe on it in both schemes and both densities, leaving a screenshot of each in `test-results/`; browsers once with `npm run e2e:install`). The theme is `src/theme.ts`, built from `src/design/tokens.ts`.

Adding a module: export a `ModuleManifest` and add it to `src/modules/index.ts`. Every route and navigation entry declares its permission (`null` for any signed-in principal); a missing one throws in development.

The grid rule: nothing imports `ag-grid-*` except `src/components/data-grid/` (ESLint enforces it; `src/lint.test.ts` proves it). A page gets a server-driven grid by declaring columns and a `fetchPage` that takes the ListQuery string.

The colour rule: no hex, `rgb()`, `hsl()`, CSS colour name or Mantine palette name in a `style`, `styles`, `vars` or `gradient` object or in `c`, `bg`, `bd`, `color`, `fill` or `stroke`, anywhere under `src/` except `src/design/` and the grid theme (ESLint enforces it; `src/lint.test.ts` proves it and reads the `.css` files under `src/` with the same rule). Name a meaning instead: `color="danger"`, `c="dimmed"`, `var(--iron-ink)`. A title of a moment takes the display family with `<Title order={2} variant="display">`; the work area keeps the reading family.
