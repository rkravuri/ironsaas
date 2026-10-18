# Foundation shell

One React build that serves every tenant hostname (ADR-0020). Vite, React 19.3, TypeScript, Mantine 9.7.3, TanStack Query, react-i18next, Zod.

| Folder | Holds |
|---|---|
| `src/api/` | `client.ts` (the one way to call the API: problems become `ApiError` with the correlation id), `identity.ts` (schemas and calls of the Identity module) |
| `src/auth/` | the session provider (`/me`), the login page, the invitation page |
| `src/modules/` | `registry.ts` (manifests, permission filtering, strict validation), `index.ts` (the list), one folder per module with its manifest and pages |
| `src/shell/` | the layout (header, navigation, sign out), the guards, the error boundary |
| `src/components/data-grid/` | `DataGrid` (AG Grid Community, infinite row model), `list-query.ts` (the ListQuery mapping), the Mantine-variable theme; the only folder that imports ag-grid (ADR-0021) |
| `src/i18n/` | the English catalogue; every string goes through it |
| `src/test/` | the test setup and the render and fetch helpers |

Commands: `npm run dev` (Vite on :5173, API paths proxied to `IRON_API_URL`, default http://localhost:5100), `npm run check` (lint, tests, build), `npm run build`.

Adding a module: export a `ModuleManifest` and add it to `src/modules/index.ts`. Every route and navigation entry declares its permission (`null` for any signed-in principal); a missing one throws in development.

The grid rule: nothing imports `ag-grid-*` except `src/components/data-grid/` (ESLint enforces it; `src/lint.test.ts` proves it). A page gets a server-driven grid by declaring columns and a `fetchPage` that takes the ListQuery string.
