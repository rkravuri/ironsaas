// @vitest-environment node
import { describe, expect, it } from "vitest";
import { ESLint, type Linter } from "eslint";

// Every stylesheet under src, read at transform time; src/design is where values are allowed to live.
const stylesheets = import.meta.glob<string>(["/src/**/*.css", "!/src/design/**"], { query: "?raw", import: "default", eager: true });
// A real page path: type-checked linting wants a file that belongs to the project.
const pagePath = "src/modules/kernel/identity/pages/UsersPage.tsx";
const colourRule = "no-restricted-syntax";

const findings = (result: ESLint.LintResult | undefined, ruleId: string): Linter.LintMessage[] => (result?.messages ?? []).filter((message) => message.ruleId === ruleId);

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
  it("refuse an ag-grid import in a page and allow it in the wrapper", async () => {
    const eslint = new ESLint();
    const source = 'import { AgGridReact } from "ag-grid-react";\nexport const grid = AgGridReact;\n';

    const [inPage] = await eslint.lintText(source, { filePath: pagePath });
    expect(inPage?.messages.map((m) => m.ruleId)).toContain("no-restricted-imports");

    const [inWrapper] = await eslint.lintText(source, { filePath: "src/components/data-grid/DataGrid.tsx" });
    expect(findings(inWrapper, "no-restricted-imports")).toEqual([]);
  }, 60_000);

  /** The layer rule (ADR-0029): a shell module imports downward only, from kernel to business to products. */
  describe("the layer rule", () => {
    const importRule = "no-restricted-imports";
    const probe = (sources: string[]): string =>
      `${sources.map((source, index) => `import { value${index} } from "${source}";`).join("\n")}\nexport const probe = [${sources.map((_, index) => `value${index}`).join(", ")}];\n`;

    /** The layer patterns the configuration gives a file, applied to an import source as ESLint applies them. */
    const refusedBy = async (eslint: ESLint, filePath: string, source: string): Promise<boolean> => {
      const config = (await eslint.calculateConfigForFile(filePath)) as { rules?: Record<string, unknown> };
      const [, options] = (config.rules?.[importRule] ?? []) as [unknown, { patterns?: { regex?: string; caseSensitive?: boolean }[] }?];
      return (options?.patterns ?? []).some((pattern) => pattern.regex !== undefined && new RegExp(pattern.regex, pattern.caseSensitive ? "u" : "iu").test(source));
    };

    it("refuses business and products imports in a kernel module, by alias and by relative path", async () => {
      const eslint = new ESLint();
      const refused = ["@/modules/business/organisations/manifest", "../../products/procurement/manifest", "@/modules/products"];
      const allowed = ["@/modules/kernel/identity/pages/UsersPage", "./products/local", "@/components/data-grid", "@/modules/registry"];

      const [result] = await eslint.lintText(probe([...refused, ...allowed]), { filePath: pagePath });
      const messages = findings(result, importRule);

      expect(messages.map((message) => message.line)).toEqual([1, 2, 3]);
      expect(messages.every((message) => message.message.includes("dependencies go downward only (ADR-0029)"))).toBe(true);
    }, 60_000);

    it("refuses products imports in a business module and allows the kernel", async () => {
      const eslint = new ESLint();
      const businessPage = "src/modules/business/organisations/pages/UnitsPage.tsx";

      expect(await refusedBy(eslint, businessPage, "@/modules/products/procurement/manifest")).toBe(true);
      expect(await refusedBy(eslint, businessPage, "../../../products/procurement/manifest")).toBe(true);
      expect(await refusedBy(eslint, businessPage, "@/modules/kernel/identity/manifest")).toBe(false);
      expect(await refusedBy(eslint, businessPage, "@/modules/business/items/manifest")).toBe(false);
    }, 60_000);

    it("keeps the grid rule inside the layers and leaves the products layer and the module list free of layer patterns", async () => {
      const eslint = new ESLint();
      const ruleFor = async (path: string): Promise<{ group?: string[]; regex?: string }[]> => {
        const config = (await eslint.calculateConfigForFile(path)) as { rules?: Record<string, unknown> };
        const [, options] = (config.rules?.[importRule] ?? []) as [unknown, { patterns?: { group?: string[]; regex?: string }[] }?];
        return options?.patterns ?? [];
      };

      for (const path of [pagePath, "src/modules/business/organisations/manifest.tsx", "src/modules/products/procurement/manifest.tsx"]) {
        expect(
          (await ruleFor(path)).some((pattern) => pattern.group?.includes("ag-grid-*")),
          path,
        ).toBe(true);
      }
      expect(await refusedBy(eslint, "src/modules/products/procurement/manifest.tsx", "@/modules/business/organisations/manifest")).toBe(false);
      expect(await refusedBy(eslint, "src/modules/index.ts", "@/modules/products/procurement/manifest")).toBe(false);
    }, 60_000);
  });

  /** The colour rule (ADR-0024 item 8): a page names a meaning, never a hue or a value. */
  describe("the colour rule", () => {
    it("refuses hex, rgb(), hsl(), palette and CSS colour names in the style and colour props of a page", async () => {
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

    it("allows theme colour names, dimmed, tokens and variables", async () => {
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

    it("leaves the design folder and the grid theme out", async () => {
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
    it("refuses a hex, an rgb() and a colour name in a declaration value", async () => {
      const eslint = new ESLint();
      const css = ".a { color: #333; border: 1px solid rgb(1, 2, 3); }\n.b:hover { background: Red }";
      expect(await rawColoursInStylesheet(eslint, css)).toEqual(["#333", "1px solid rgb(1, 2, 3)", "Red"]);
    }, 60_000);

    it("allows variables, non-colour keywords and a selector that is a colour name", async () => {
      const eslint = new ESLint();
      const css = ".red { display: flex; color: var(--mantine-color-red-6); transition: none; }";
      expect(await rawColoursInStylesheet(eslint, css)).toEqual([]);
    }, 60_000);

    it("finds no colour by value in the stylesheets under src outside src/design", async () => {
      const eslint = new ESLint();
      const files = Object.entries(stylesheets).sort(([first], [second]) => first.localeCompare(second));
      for (const [file, css] of files) {
        expect(await rawColoursInStylesheet(eslint, css), file).toEqual([]);
      }
    }, 60_000);
  });
});
