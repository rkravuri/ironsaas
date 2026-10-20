import type { Rule } from "eslint";

/** The suite rule (ADR-0043): a module of one suite never imports from another suite's folder. */
export declare const suiteImports: Rule.RuleModule;
