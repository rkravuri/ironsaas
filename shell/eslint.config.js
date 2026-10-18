import js from "@eslint/js";
import globals from "globals";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import tseslint from "typescript-eslint";

// The grid rule (S1 plan 3.10): nothing imports ag-grid except the DataGrid wrapper, so the Enterprise switch is one place.
const gridWrapper = ["src/components/data-grid/**"];

export default tseslint.config(
  { ignores: ["dist", "node_modules", "coverage"] },
  {
    files: ["**/*.{ts,tsx}"],
    extends: [js.configs.recommended, ...tseslint.configs.recommendedTypeChecked],
    languageOptions: {
      ecmaVersion: 2023,
      globals: globals.browser,
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    plugins: { "react-hooks": reactHooks, "react-refresh": reactRefresh },
    rules: {
      ...reactHooks.configs.recommended.rules,
      "react-refresh/only-export-components": ["warn", { allowConstantExport: true }],
      "no-restricted-imports": [
        "error",
        {
          patterns: [
            {
              group: ["ag-grid-*", "@ag-grid-community/*", "@ag-grid-enterprise/*"],
              message: "Import DataGrid from '@/components/data-grid' instead; only the wrapper talks to AG Grid.",
            },
          ],
        },
      ],
    },
  },
  {
    files: gridWrapper,
    rules: { "no-restricted-imports": "off" },
  },
  {
    files: ["eslint.config.js", "vite.config.ts"],
    extends: [tseslint.configs.disableTypeChecked],
  },
);
