import js from "@eslint/js";
import globals from "globals";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import tseslint from "typescript-eslint";
import { ironPlugin } from "./lint/plugin.js";

// The grid rule (S1 plan 3.10): nothing imports ag-grid except the DataGrid wrapper, so the Enterprise switch is one place.
const gridWrapper = ["src/components/data-grid/**"];
const gridImports = {
  group: ["ag-grid-*", "@ag-grid-community/*", "@ag-grid-enterprise/*"],
  message: "Import DataGrid from '@/components/data-grid' instead; only the wrapper talks to AG Grid.",
};

// The layer rule (ADR-0029, ADR-0043): shell modules live in modules/kernel, modules/business and
// modules/suites/<suite>, and import downward only, as the .NET projects reference. The pattern sees both
// "@/modules/business/x" and "../../business/x"; a module's own folder named business or suites ("./business") is not a
// layer. A suite never imports from another suite: the local rule iron/suite-imports reads the suite from the file's
// path, which a pattern cannot. modules/index.ts composes every layer and suite.
const layerImports = (layer, above) => ({
  regex: `(^|/)(modules|[.][.])/(${above.join("|")})(/|$)`,
  caseSensitive: true,
  message: `A ${layer} module must not import from ${above.map((name) => `modules/${name}`).join(" or ")}: dependencies go downward only (ADR-0029).`,
});

// The colour rule (ADR-0024 item 8, ADR-0025): a page names a meaning, never a hue or a value. The 148 CSS colour
// keywords plus the two Mantine palette names that are not CSS keywords; a Mantine shade suffix (red.6) is refused
// with its name. The guards around a name keep var(--mantine-color-red-6) and "redirect" out of it.
const cssColourKeywords = [
  "aliceblue",
  "antiquewhite",
  "aqua",
  "aquamarine",
  "azure",
  "beige",
  "bisque",
  "black",
  "blanchedalmond",
  "blue",
  "blueviolet",
  "brown",
  "burlywood",
  "cadetblue",
  "chartreuse",
  "chocolate",
  "coral",
  "cornflowerblue",
  "cornsilk",
  "crimson",
  "cyan",
  "darkblue",
  "darkcyan",
  "darkgoldenrod",
  "darkgray",
  "darkgreen",
  "darkgrey",
  "darkkhaki",
  "darkmagenta",
  "darkolivegreen",
  "darkorange",
  "darkorchid",
  "darkred",
  "darksalmon",
  "darkseagreen",
  "darkslateblue",
  "darkslategray",
  "darkslategrey",
  "darkturquoise",
  "darkviolet",
  "deeppink",
  "deepskyblue",
  "dimgray",
  "dimgrey",
  "dodgerblue",
  "firebrick",
  "floralwhite",
  "forestgreen",
  "fuchsia",
  "gainsboro",
  "ghostwhite",
  "gold",
  "goldenrod",
  "gray",
  "green",
  "greenyellow",
  "grey",
  "honeydew",
  "hotpink",
  "indianred",
  "indigo",
  "ivory",
  "khaki",
  "lavender",
  "lavenderblush",
  "lawngreen",
  "lemonchiffon",
  "lightblue",
  "lightcoral",
  "lightcyan",
  "lightgoldenrodyellow",
  "lightgray",
  "lightgreen",
  "lightgrey",
  "lightpink",
  "lightsalmon",
  "lightseagreen",
  "lightskyblue",
  "lightslategray",
  "lightslategrey",
  "lightsteelblue",
  "lightyellow",
  "lime",
  "limegreen",
  "linen",
  "magenta",
  "maroon",
  "mediumaquamarine",
  "mediumblue",
  "mediumorchid",
  "mediumpurple",
  "mediumseagreen",
  "mediumslateblue",
  "mediumspringgreen",
  "mediumturquoise",
  "mediumvioletred",
  "midnightblue",
  "mintcream",
  "mistyrose",
  "moccasin",
  "navajowhite",
  "navy",
  "oldlace",
  "olive",
  "olivedrab",
  "orange",
  "orangered",
  "orchid",
  "palegoldenrod",
  "palegreen",
  "paleturquoise",
  "palevioletred",
  "papayawhip",
  "peachpuff",
  "peru",
  "pink",
  "plum",
  "powderblue",
  "purple",
  "rebeccapurple",
  "red",
  "rosybrown",
  "royalblue",
  "saddlebrown",
  "salmon",
  "sandybrown",
  "seagreen",
  "seashell",
  "sienna",
  "silver",
  "skyblue",
  "slateblue",
  "slategray",
  "slategrey",
  "snow",
  "springgreen",
  "steelblue",
  "tan",
  "teal",
  "thistle",
  "tomato",
  "turquoise",
  "violet",
  "wheat",
  "white",
  "whitesmoke",
  "yellow",
  "yellowgreen",
];
const colourNames = [...cssColourKeywords, "dark", "grape"].join("|");
// Character classes rather than escapes: the pattern travels inside a selector string, where a backslash is fragile.
const rawColour =
  "/#(?:[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{3,4})(?![0-9a-z])" +
  "|(?<![A-Za-z0-9_-])(?:rgba?|hsla?)[(]" +
  `|(?<![A-Za-z0-9_.-])(?:${colourNames})(?:[.][0-9]{1,2})?(?![A-Za-z0-9_-])/i`;
const colourPositions = "JSXAttribute[name.name=/^(?:style|styles|vars|gradient|c|bg|bd|color|fill|stroke)$/]";
const colourRule = {
  selector: `${colourPositions} :matches(Literal[value=${rawColour}], TemplateElement[value.raw=${rawColour}])`,
  message:
    "Colour by value is not allowed in a page (ADR-0024). Name a meaning instead: a theme colour (primary, danger, success, warning, info or the module's identity colour), dimmed, or a CSS variable from src/design/tokens.ts; never a hex, rgb(), hsl(), a palette name or a CSS colour name.",
};

export default tseslint.config(
  { ignores: ["dist", "dist-gallery", "node_modules", "coverage"] },
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
      "no-restricted-imports": ["error", { patterns: [gridImports] }],
    },
  },
  {
    files: gridWrapper,
    rules: { "no-restricted-imports": "off" },
  },
  {
    // A later block replaces the rule's options, so the grid pattern is repeated with each layer's.
    files: ["src/modules/kernel/**/*.{ts,tsx}"],
    rules: {
      "no-restricted-imports": ["error", { patterns: [gridImports, layerImports("kernel", ["business", "suites"])] }],
    },
  },
  {
    files: ["src/modules/business/**/*.{ts,tsx}"],
    rules: { "no-restricted-imports": ["error", { patterns: [gridImports, layerImports("business", ["suites"])] }] },
  },
  {
    files: ["src/modules/suites/**/*.{ts,tsx}"],
    plugins: { iron: ironPlugin },
    rules: { "iron/suite-imports": "error" },
  },
  {
    // What a test proves (ADR-0044): every Vitest and Playwright test's title ends with the criteria or decisions it
    // proves, which make trace reads.
    files: ["src/**/*.test.{ts,tsx}", "e2e/**/*.spec.ts"],
    plugins: { iron: ironPlugin },
    rules: { "iron/test-proves": "error" },
  },
  {
    // Where HTML reaches the page (TF-071 AC5, ADR-0053): only the rich-text component renders it.
    files: ["src/**/*.{ts,tsx}"],
    ignores: ["src/components/rich-text/RichTextView.tsx"],
    plugins: { iron: ironPlugin },
    rules: { "iron/inner-html": "error" },
  },
  {
    // Everything under src, except where the values are defined and where they are mapped into the grid.
    files: ["src/**/*.{ts,tsx}"],
    ignores: ["src/design/**", "src/components/data-grid/theme.ts"],
    rules: { "no-restricted-syntax": ["error", colourRule] },
  },
  {
    files: [
      "eslint.config.js",
      "lint/inner-html.js",
      "lint/inner-html.d.ts",
      "lint/plugin.js",
      "lint/plugin.d.ts",
      "lint/suite-imports.js",
      "lint/suite-imports.d.ts",
      "lint/test-proves.js",
      "lint/test-proves.d.ts",
      "vite.config.ts",
      "playwright.config.ts",
      "playwright.a11y.config.ts",
    ],
    extends: [tseslint.configs.disableTypeChecked],
  },
  {
    files: ["e2e/**/*.ts"],
    languageOptions: { globals: { ...globals.browser, ...globals.node } },
    rules: { "react-refresh/only-export-components": "off" },
  },
);
