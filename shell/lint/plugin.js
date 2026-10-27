// The shell's own lint rules, under the prefix "iron/": the suite rule (ADR-0043), what a test proves (ADR-0044), and
// where HTML may reach the page (ADR-0053).
import { innerHtml } from "./inner-html.js";
import { suiteImports } from "./suite-imports.js";
import { testProves } from "./test-proves.js";

export const ironPlugin = {
  meta: { name: "iron" },
  rules: { "inner-html": innerHtml, "suite-imports": suiteImports, "test-proves": testProves },
};
