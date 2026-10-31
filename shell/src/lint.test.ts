// @vitest-environment node
import { describe, expect, it } from "vitest";
import { ESLint, Linter } from "eslint";
import { ironPlugin } from "../lint/plugin.js";

// Every stylesheet under src, read at transform time; src/design is where values are allowed to live. The call has no
// type argument, so Vitest sees it and never caches this file's transform with a stale list of stylesheets.
const stylesheets: Record<string, string> = import.meta.glob(["/src/**/*.css", "!/src/design/**"], {
  query: "?raw",
  import: "default",
  eager: true,
});
// A real page path: type-checked linting wants a file that belongs to the project.
const pagePath = "src/modules/kernel/identity/pages/UsersPage.tsx";
const colourRule = "no-restricted-syntax";

const findings = (result: ESLint.LintResult | undefined, ruleId: string): Linter.LintMessage[] =>
  (result?.messages ?? []).filter((message) => message.ruleId === ruleId);

/** Every declaration value of a stylesheet, asked the same question the colour rule asks of a style prop. */
async function rawColoursInStylesheet(eslint: ESLint, css: string): Promise<string[]> {
  const declarations = css.replace(/\/\*[\s\S]*?\*\//g, "").matchAll(/[\w-]+\s*:\s*([^;{}]+)(?=[;}])/g);
  const values = [...declarations].map((match) => (match[1] ?? "").trim());
  if (values.length === 0) {
    return [];
  }
  // Lines 1 to 3 are the wrapper; value i is on line i + 4.
  const probe = `export const probe = (\n  <div\n    style={{\n${values.map((value, index) => `      v${index}: ${JSON.stringify(value)},`).join("\n")}\n    }}\n  />\n);\n`;
  const [result] = await eslint.lintText(probe, { filePath: pagePath });
  return findings(result, colourRule).map((message) => values[message.line - 4] ?? "?");
}

describe("the lint rules", () => {
  /** The grid rule (S1 plan 3.10): a raw ag-grid import anywhere but the wrapper fails lint. */
  it("refuse an ag-grid import in a page and allow it in the wrapper [TF-171 AC1, ADR-0021]", async () => {
    const eslint = new ESLint();
    const source = 'import { AgGridReact } from "ag-grid-react";\nexport const grid = AgGridReact;\n';

    const [inPage] = await eslint.lintText(source, { filePath: pagePath });
    expect(inPage?.messages.map((m) => m.ruleId)).toContain("no-restricted-imports");

    const [inWrapper] = await eslint.lintText(source, { filePath: "src/components/data-grid/DataGrid.tsx" });
    expect(findings(inWrapper, "no-restricted-imports")).toEqual([]);
  }, 60_000);

  /** The layer rule (ADR-0029, ADR-0043): a shell module imports downward only, from kernel to business to the suites. */
  describe("the layer rule", () => {
    const importRule = "no-restricted-imports";
    const probe = (sources: string[]): string =>
      `${sources.map((source, index) => `import { value${index} } from "${source}";`).join("\n")}\nexport const probe = [${sources.map((_, index) => `value${index}`).join(", ")}];\n`;

    /** The layer patterns the configuration gives a file, applied to an import source as ESLint applies them. */
    const refusedBy = async (eslint: ESLint, filePath: string, source: string): Promise<boolean> => {
      const config = (await eslint.calculateConfigForFile(filePath)) as { rules?: Record<string, unknown> };
      const [, options] = (config.rules?.[importRule] ?? []) as [
        unknown,
        { patterns?: { regex?: string; caseSensitive?: boolean }[] }?,
      ];
      return (options?.patterns ?? []).some(
        (pattern) =>
          pattern.regex !== undefined && new RegExp(pattern.regex, pattern.caseSensitive ? "u" : "iu").test(source),
      );
    };

    it("refuses business and suite imports in a kernel module, by alias and by relative path [ADR-0029, ADR-0043]", async () => {
      const eslint = new ESLint();
      const refused = [
        "@/modules/business/organisations/manifest",
        "../../suites/procurement/orders/manifest",
        "@/modules/suites",
      ];
      const allowed = [
        "@/modules/kernel/identity/pages/UsersPage",
        "./suites/local",
        "@/components/data-grid",
        "@/modules/registry",
      ];

      const [result] = await eslint.lintText(probe([...refused, ...allowed]), { filePath: pagePath });
      const messages = findings(result, importRule);

      expect(messages.map((message) => message.line)).toEqual([1, 2, 3]);
      expect(messages.every((message) => message.message.includes("dependencies go downward only (ADR-0029)"))).toBe(
        true,
      );
    }, 60_000);

    it("refuses suite imports in a business module and allows the kernel [ADR-0029, ADR-0043]", async () => {
      const eslint = new ESLint();
      const businessPage = "src/modules/business/organisations/pages/UnitsPage.tsx";

      expect(await refusedBy(eslint, businessPage, "@/modules/suites/procurement/orders/manifest")).toBe(true);
      expect(await refusedBy(eslint, businessPage, "../../../suites/procurement/orders/manifest")).toBe(true);
      expect(await refusedBy(eslint, businessPage, "@/modules/kernel/identity/manifest")).toBe(false);
      expect(await refusedBy(eslint, businessPage, "@/modules/business/items/manifest")).toBe(false);
    }, 60_000);

    it("keeps the grid rule inside the layers and leaves the suites and the module list free of layer patterns [ADR-0021, ADR-0043]", async () => {
      const eslint = new ESLint();
      const ruleFor = async (path: string): Promise<{ group?: string[]; regex?: string }[]> => {
        const config = (await eslint.calculateConfigForFile(path)) as { rules?: Record<string, unknown> };
        const [, options] = (config.rules?.[importRule] ?? []) as [
          unknown,
          { patterns?: { group?: string[]; regex?: string }[] }?,
        ];
        return options?.patterns ?? [];
      };

      for (const path of [
        pagePath,
        "src/modules/business/organisations/manifest.tsx",
        "src/modules/suites/procurement/orders/manifest.tsx",
      ]) {
        expect(
          (await ruleFor(path)).some((pattern) => pattern.group?.includes("ag-grid-*")),
          path,
        ).toBe(true);
      }
      expect(
        await refusedBy(
          eslint,
          "src/modules/suites/procurement/orders/manifest.tsx",
          "@/modules/business/organisations/manifest",
        ),
      ).toBe(false);
      expect(await refusedBy(eslint, "src/modules/index.ts", "@/modules/suites/procurement/orders/manifest")).toBe(
        false,
      );
    }, 60_000);
  });

  /** The suite rule (ADR-0043): a suite's module never imports from another suite, whichever way the path is written. */
  describe("the suite rule", () => {
    const suiteRule = "iron/suite-imports";
    const probe = (sources: string[]): string =>
      `${sources.map((source, index) => `import { value${index} } from "${source}";`).join("\n")}\nexport const probe = [${sources.map((_, index) => `value${index}`).join(", ")}];\n`;
    const verify = (filename: string, sources: string[]): Linter.LintMessage[] =>
      new Linter().verify(
        probe(sources),
        [{ files: ["**/*.ts"], plugins: { iron: ironPlugin }, rules: { [suiteRule]: "error" } }],
        { filename },
      );

    it("refuses an import from another suite by alias and by relative path, and allows the suite's own and the layers below [ADR-0043]", () => {
      const ordersPage = "src/modules/suites/procurement/orders/pages/OrdersPage.ts";
      const refused = [
        "@/modules/suites/school/pupils/manifest",
        "../../../school/pupils/manifest",
        "@/modules/suites/school",
      ];
      const allowed = [
        "@/modules/suites/procurement/purchasing/manifest",
        "../../purchasing/manifest",
        "./OrderLines",
        "@/modules/business/organisations/manifest",
        "@/modules/kernel/identity/manifest",
        "@/components/data-grid",
        "@mantine/core",
      ];

      const messages = verify(ordersPage, [...refused, ...allowed]);

      expect(messages.map((message) => message.line)).toEqual([1, 2, 3]);
      expect(messages.every((message) => message.ruleId === suiteRule)).toBe(true);
      expect(messages[0]?.message).toBe(
        "A module of the procurement suite must not import from the school suite: a suite never references another suite (ADR-0043). What both need belongs in a business module.",
      );
    });

    it("knows the suite from the path whether it is relative to the shell or absolute [ADR-0043]", () => {
      // As ESLint itself passes it: absolute, under the shell's root.
      const absolute = `${process.cwd()}/src/modules/suites/school/pupils/manifest.ts`;
      expect(verify(absolute, ["@/modules/suites/procurement/orders/manifest"])).toHaveLength(1);
      expect(verify(absolute, ["../../procurement/orders/manifest"])).toHaveLength(1);
      expect(verify(absolute, ["../classes/manifest"])).toEqual([]);
      // Outside the suites the rule has nothing to say; the layer rule and modules/index.ts cover the rest.
      expect(verify("src/modules/index.ts", ["@/modules/suites/procurement/orders/manifest"])).toEqual([]);
    });

    it("is on for every file under modules/suites and nowhere else [ADR-0043]", async () => {
      const eslint = new ESLint();
      const ruleFor = async (path: string): Promise<unknown> => {
        const config = (await eslint.calculateConfigForFile(path)) as { rules?: Record<string, unknown> };
        return config.rules?.[suiteRule];
      };
      expect(await ruleFor("src/modules/suites/procurement/orders/manifest.tsx")).toEqual([2]);
      expect(await ruleFor(pagePath)).toBeUndefined();
      expect(await ruleFor("src/modules/business/organisations/manifest.tsx")).toBeUndefined();
    }, 60_000);
  });

  /** What a test proves (ADR-0044): a Vitest or Playwright test's title ends with the criteria or decisions it proves. */
  describe("the inner HTML rule", () => {
    const innerHtmlRule = "iron/inner-html";
    const source = `${[
      'export const a = <div dangerouslySetInnerHTML={{ __html: "<b>x</b>" }} />;',
      'export const b = { dangerouslySetInnerHTML: { __html: "x" } };',
      'export const c = (e: HTMLElement) => { e.innerHTML = "<b>x</b>"; };',
      'export const d = (e: HTMLElement) => { e["outerHTML"] = "x"; };',
      'export const f = (e: HTMLElement) => e.insertAdjacentHTML("beforeend", "x");',
      'export const g = () => document.write("x");',
      'export const h = <div>{"<b>shown as text</b>"}</div>;',
      'export const i = (e: HTMLElement) => { e.textContent = "<b>x</b>"; };',
    ].join("\n")}\n`;

    it("refuses every way of putting HTML into a page and lets text be text [TF-071 AC5, ADR-0053]", async () => {
      const eslint = new ESLint();

      const [inPage] = await eslint.lintText(source, { filePath: pagePath });

      expect(findings(inPage, innerHtmlRule).map((message) => message.line)).toEqual([1, 2, 3, 4, 5, 6]);
    }, 60_000);

    it("allows HTML in the one rich-text component [TF-071 AC5, ADR-0053]", async () => {
      const eslint = new ESLint();

      const [inComponent] = await eslint.lintText(source, { filePath: "src/components/rich-text/RichTextView.tsx" });

      expect(findings(inComponent, innerHtmlRule)).toEqual([]);
    }, 60_000);
  });

  describe("the test rule", () => {
    const testRule = "iron/test-proves";
    const verify = (lines: string[]): Linter.LintMessage[] =>
      new Linter().verify(
        `${lines.join("\n")}\n`,
        [{ files: ["**/*.ts"], plugins: { iron: ironPlugin }, rules: { [testRule]: "error" } }],
        { filename: "src/probe.test.ts" },
      );

    it("refuses a title without the list, with an identifier out of form, untraced beside an identifier, or not a literal [ADR-0044]", () => {
      const messages = verify([
        'it("works", () => {});',
        'test("says [tf-033 ac2]", () => {});',
        'it.skip("waits [TF-33 AC2, ADR-29]", () => {});',
        "it(`in the ${scheme} scheme`, () => {});",
        'it("proves nothing and something [untraced, TF-033 AC2]", () => {});',
        "it(title, () => {});",
        'it.each([1, 2])("row %s", () => {});',
      ]);

      expect(messages.map((message) => [message.line, message.messageId])).toEqual([
        [1, "missing"],
        [2, "malformed"],
        [3, "malformed"],
        [3, "malformed"],
        [4, "missing"],
        [5, "untracedAlone"],
        [6, "notLiteral"],
        [7, "missing"],
      ]);
      expect(messages.every((message) => message.ruleId === testRule)).toBe(true);
    });

    it("allows criteria, requirements, decisions and untraced, in strings and at the end of templates [ADR-0044]", () => {
      const messages = verify([
        'it("a child-unit administrator sees only its users [TF-033 AC2]", () => {});',
        'test.fixme("proves two [TF-033 AC1, TF-033 AC3, ADR-0030]", async () => {});',
        "test(`in the ${scheme} scheme [TF-172 AC1]`, async () => {});",
        'it.each([1, 2])("row %s [REQ-012 AC1]", () => {});',
        'it("draws a pattern [untraced]", () => {});',
        'it("a whole requirement [TF-101]", () => {});',
      ]);

      expect(messages).toEqual([]);
    });

    it("leaves describe, step, hooks and Playwright's conditional skip alone [ADR-0044]", () => {
      const messages = verify([
        'describe("a group", () => {});',
        'test.describe("a group", () => {});',
        'test.skip(process.env.CI === undefined, "needs the stack");',
        "test.beforeEach(async () => {});",
        'test.step("a step", async () => {});',
        'expect("text").toBe("text");',
      ]);

      expect(messages).toEqual([]);
    });

    it("is on for the shell's tests and browser specs and nowhere else [ADR-0044]", async () => {
      const eslint = new ESLint();
      const ruleFor = async (path: string): Promise<unknown> => {
        const config = (await eslint.calculateConfigForFile(path)) as { rules?: Record<string, unknown> };
        return config.rules?.[testRule];
      };
      expect(await ruleFor("src/auth/LoginPage.test.tsx")).toEqual([2]);
      expect(await ruleFor("e2e/login.spec.ts")).toEqual([2]);
      expect(await ruleFor("e2e/a11y/users.spec.ts")).toEqual([2]);
      expect(await ruleFor(pagePath)).toBeUndefined();
    }, 60_000);
  });

  /** The colour rule (ADR-0024 item 8): a page names a meaning, never a hue or a value. */
  describe("the colour rule", () => {
    it("refuses hex, rgb(), hsl(), palette and CSS colour names in the style and colour props of a page [ADR-0025]", async () => {
      const eslint = new ESLint();
      const source = [
        'import { Alert, Text } from "@mantine/core";',
        "export const probe = (",
        "  <div>",
        '    <div style={{ color: "#333333", background: "rgb(1, 2, 3)" }} />',
        "    <div bd={`1px solid #ccc`} />",
        '    <Alert color="red" styles={{ root: { borderColor: "hsl(0 0% 50%)" } }} />',
        '    <Text c="gray.6" bg="white" />',
        "  </div>",
        ");",
        "",
      ].join("\n");
      const [result] = await eslint.lintText(source, { filePath: pagePath });
      const messages = findings(result, colourRule);
      expect(messages.map((message) => message.line)).toEqual([4, 4, 5, 6, 6, 7, 7]);
      expect(messages.every((message) => message.message.includes("Colour by value"))).toBe(true);
    }, 60_000);

    it("allows theme colour names, dimmed, tokens and variables [ADR-0025]", async () => {
      const eslint = new ESLint();
      const source = [
        'import { Alert, Text } from "@mantine/core";',
        "export const probe = (",
        "  <div>",
        '    <Alert color="danger" />',
        '    <Text c="dimmed" style={{ color: "var(--iron-ink)", height: "calc(100vh - 56px)" }} />',
        '    <div bd="1px solid var(--mantine-color-default-border)" />',
        '    <svg fill="currentColor" stroke="none" />',
        "  </div>",
        ");",
        "",
      ].join("\n");
      const [result] = await eslint.lintText(source, { filePath: pagePath });
      expect(findings(result, colourRule)).toEqual([]);
    }, 60_000);

    it("leaves the design folder and the grid theme out [ADR-0025]", async () => {
      const eslint = new ESLint();
      const ruleFor = async (path: string): Promise<unknown> => {
        const config = (await eslint.calculateConfigForFile(path)) as { rules?: Record<string, unknown> };
        return config.rules?.[colourRule];
      };
      expect(await ruleFor("src/design/tokens.ts")).toBeUndefined();
      expect(await ruleFor("src/components/data-grid/theme.ts")).toBeUndefined();
      expect(await ruleFor("src/auth/LoginPage.tsx")).toBeDefined();
    }, 60_000);
  });

  /** The stylesheets get the same rule through this test, since the build has no CSS linter (ADR-0025). */
  describe("the CSS guard", () => {
    it("refuses a hex, an rgb() and a colour name in a declaration value [ADR-0025]", async () => {
      const eslint = new ESLint();
      const css = ".a { color: #333; border: 1px solid rgb(1, 2, 3); }\n.b:hover { background: Red }";
      expect(await rawColoursInStylesheet(eslint, css)).toEqual(["#333", "1px solid rgb(1, 2, 3)", "Red"]);
    }, 60_000);

    it("allows variables, non-colour keywords and a selector that is a colour name [ADR-0025]", async () => {
      const eslint = new ESLint();
      const css = ".red { display: flex; color: var(--mantine-color-red-6); transition: none; }";
      expect(await rawColoursInStylesheet(eslint, css)).toEqual([]);
    }, 60_000);

    it("finds no colour by value in the stylesheets under src outside src/design [ADR-0025]", async () => {
      const eslint = new ESLint();
      const files = Object.entries(stylesheets).sort(([first], [second]) => first.localeCompare(second));
      for (const [file, css] of files) {
        expect(await rawColoursInStylesheet(eslint, css), file).toEqual([]);
      }
    }, 60_000);
  });

  /** A glob Vitest can see, so it never caches a file whose list of files depends on other files (ADR-0004, since V-close). */
  describe("the glob rule", () => {
    it("refuses a typed import.meta.glob and allows one whose variable carries the type [ADR-0004]", async () => {
      const eslint = new ESLint();
      const typed = 'export const files = import.meta.glob<string>("./*.css", { eager: true });\n';
      const untyped = 'export const files: Record<string, string> = import.meta.glob("./*.css", { eager: true });\n';

      const [refused] = await eslint.lintText(typed, { filePath: pagePath });
      const [allowed] = await eslint.lintText(untyped, { filePath: pagePath });

      expect(findings(refused, "no-restricted-syntax").map((message) => message.message)).toEqual([
        expect.stringContaining("Write import.meta.glob(...) without a type argument") as string,
      ]);
      expect(findings(allowed, "no-restricted-syntax")).toEqual([]);
    }, 60_000);
  });
});
