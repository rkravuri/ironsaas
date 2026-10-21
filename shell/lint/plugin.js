// The shell's own lint rules, under the prefix "iron/": the suite rule (ADR-0043) and what a test proves (ADR-0044).
import { suiteImports } from "./suite-imports.js";
import { testProves } from "./test-proves.js";

export const ironPlugin = {
  meta: { name: "iron" },
  rules: { "suite-imports": suiteImports, "test-proves": testProves },
};
