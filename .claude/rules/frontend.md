# Frontend rules (the shell)

The shell is one React build for every tenant (ADR-0020). The design language is ADR-0024 and `docs/design/design-language.md`; read section 9 before finishing a page.

- Modules live in `shell/src/modules/<layer>/<module>/` (`kernel`, `business` or `products`, ADR-0029) with a manifest (routes and navigation, each with its permission), pages, and an API binding in `shell/src/api/<module>.ts` with Zod schemas.
- Imports between modules go downward only: a kernel module never imports from `modules/business` or `modules/products`, a business module never from `modules/products` (ESLint refuses it). `modules/index.ts` is the one list of manifests and the one file that imports every layer.
- Every call goes through `request()`; problems are `ApiError` with the correlation id; error views show it.
- Lists use `DataGrid` (the only place that imports AG Grid); forms use the design system's form; pages use the templates once D3 lands.
- Colour, type, space and motion come from `shell/src/design/tokens.ts` and the theme; no colour by value, no palette name in a page; status is never colour alone.
- Every string goes through `src/i18n/en.json`. Labels on every control; visible focus; keyboard operation; `prefers-reduced-motion` honoured.
- Tests: Vitest with Testing Library for pages and mappings; Playwright in `e2e/` for user paths against the stack; the gallery scan with axe for accessibility.
- Drawings are original SVG in one hand (brief section 3); never a known character, mascot, brand or artist's style; accessible name or `aria-hidden`.
- Fonts and images come from their official source with the licence recorded in `docs/licences.md`; no request to any external host (the CSP stays as it is).
