import type { ESLint } from "eslint";

/**
 * The shell's own lint rules: "iron/suite-imports" refuses an import from one suite's folder into another's (ADR-0043),
 * "iron/test-proves" a test whose title does not end with what it proves (ADR-0044), and "iron/inner-html" HTML put
 * into the page anywhere but the rich-text component (ADR-0053).
 */
export declare const ironPlugin: ESLint.Plugin;
