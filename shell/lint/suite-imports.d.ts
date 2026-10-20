import type { ESLint } from "eslint";

/** The shell's own lint rules (ADR-0043): "iron/suite-imports" refuses an import from one suite's folder into another's. */
export declare const ironPlugin: ESLint.Plugin;
