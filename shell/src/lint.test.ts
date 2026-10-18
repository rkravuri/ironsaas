// @vitest-environment node
import { describe, expect, it } from "vitest";
import { ESLint } from "eslint";

/** The grid rule (S1 plan 3.10): a raw ag-grid import anywhere but the wrapper fails lint. */
describe("the lint rules", () => {
  it("refuse an ag-grid import in a page and allow it in the wrapper", async () => {
    const eslint = new ESLint();
    const source = 'import { AgGridReact } from "ag-grid-react";\nexport const grid = AgGridReact;\n';

    const [inPage] = await eslint.lintText(source, { filePath: "src/modules/identity/pages/UsersPage.tsx" });
    expect(inPage?.messages.map((m) => m.ruleId)).toContain("no-restricted-imports");

    const [inWrapper] = await eslint.lintText(source, { filePath: "src/components/data-grid/DataGrid.tsx" });
    expect(inWrapper?.messages.filter((m) => m.ruleId === "no-restricted-imports")).toEqual([]);
  }, 60_000);
});
