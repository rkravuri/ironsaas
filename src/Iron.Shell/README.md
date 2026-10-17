# Iron.Shell

The React application shell and design system: login flows, navigation from module manifests filtered by entitlement, permission and flags, page layout patterns, global error handling with correlation ids, session expiry handling, and the product registration API. One build serves every tenant hostname.

Stack (decided 2026-10-17): Node.js v26.12.0 (see `.node-version` at the repository root), React 19.3, TypeScript, Vite, Mantine 9.7.3, TanStack Query, react-i18next, Zod, AG Grid Community wrapped in one `DataGrid` component.

Slice S1 builds the shell foundation in T15 and the `DataGrid` component with the Users page in T16. Exact package versions are pinned in `package.json` when T15 lands.
