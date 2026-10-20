# Suite modules

The shell modules of the suites (ADR-0043), one folder per suite by its short name, then one per module: `suites/procurement/<name>/` for the eProcurement Suite. None yet.

- A suite module's manifest declares `suite: "<suite>"` (its folder's name) and its `licence`; `src/modules/placement.test.ts` holds the suite to the folder, and the home page groups the module's opener under its suite.
- It may import from `modules/kernel`, `modules/business` and its own suite's folder. ESLint refuses an import from another suite (`iron/suite-imports`, in `lint/suite-imports.js`), and the kernel and business modules may not import from here.
- `src/modules/index.ts` lists it with every other manifest; nothing else in the shell imports it.
