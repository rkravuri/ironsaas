/**
 * The design tokens (ADR-0024, ADR-0025): the one place a colour, a size, a duration or a family is written down.
 * Everything else reads them: the Mantine theme, the CSS variables it emits, the grid, the gallery and the tests.
 */
import { accentShades, darkSteps, graySteps, oklchToHex, shadesFor, type Shades } from "./colour";

export const accentNames = ["primary", "identity", "console", "success", "warning", "danger", "info"] as const;
export type AccentName = (typeof accentNames)[number];

export interface Accent {
  readonly name: AccentName;
  /** The catalogue key of the one-line meaning the gallery shows: a colour without a meaning is not a token. */
  readonly meaningKey: string;
  readonly hue: number;
  readonly chroma: number;
  readonly shades: Shades;
}

const accent = (name: AccentName, hue: number, chroma: number): Accent => ({
  name,
  meaningKey: `design.accents.${name}`,
  hue,
  chroma,
  shades: accentShades(hue, chroma),
});

// The neutrals: the paper's warm hue for the light scheme's greys, the ink's cool hue for the dark scheme's.
const paperHue = 85;
const inkHue = 260;
const gray = shadesFor(paperHue, 0.01, { steps: graySteps });
const dark = shadesFor(inkHue, 0.015, { steps: darkSteps });

export const tokens = {
  /** Text and strokes: a near-black with a hint of blue, and its light counterpart on the dark paper. */
  ink: { light: oklchToHex({ lightness: 0.25, chroma: 0.02, hue: inkHue }), dark: dark[0] },
  /** Surfaces: a warm off-white, and a cool near-black. Pure black and pure white are not used. */
  paper: { light: oklchToHex({ lightness: 0.985, chroma: 0.006, hue: paperHue }), dark: dark[7] },
  /** The two neutral scales in Mantine's order: gray serves the light scheme, dark the dark scheme. */
  neutral: { gray, dark },
  /** One hue and one chroma each; the gallery shows the shades and the owner judges the hues there. */
  accents: {
    primary: accent("primary", 255, 0.14),
    identity: accent("identity", 300, 0.12),
    console: accent("console", 195, 0.1),
    success: accent("success", 150, 0.12),
    warning: accent("warning", 70, 0.12),
    danger: accent("danger", 25, 0.15),
    info: accent("info", 220, 0.11),
  },
  type: {
    reading: { family: '"Source Sans 3 Variable", "Source Sans 3", "Segoe UI", system-ui, sans-serif' },
    display: { family: '"Fraunces Variable", Fraunces, Georgia, "Times New Roman", serif' },
    mono: { family: 'ui-monospace, "Cascadia Mono", "Segoe UI Mono", Menlo, Consolas, monospace' },
    /** Six sizes; the display family is for the two largest only. */
    sizes: {
      xs: { px: 12, lineHeight: 16 },
      sm: { px: 14, lineHeight: 20 },
      md: { px: 16, lineHeight: 24 },
      lg: { px: 20, lineHeight: 28 },
      xl: { px: 24, lineHeight: 32 },
      xxl: { px: 32, lineHeight: 40 },
    },
  },
  spacing: { xs: 8, sm: 12, md: 16, lg: 24, xl: 32 },
  radius: { xs: 2, sm: 4, md: 8, lg: 16, xl: 32 },
  motion: {
    /** Quick for a state change under the pointer, calm for something that arrives or leaves, draw for a pattern drawing itself in. */
    duration: { quick: 150, calm: 250, draw: 900 },
    easing: { enter: "cubic-bezier(0, 0, 0.2, 1)", exit: "cubic-bezier(0.4, 0, 1, 1)" },
  },
  density: { rowHeight: { comfortable: 40, compact: 32 } },
} as const;

export type TypeSize = keyof typeof tokens.type.sizes;
export type Scheme = "light" | "dark";

/** WCAG 2.1 AA for text (1.4.3). */
export const textMinimum = 4.5;
/** WCAG 2.1 AA for borders, focus rings and other non-text marks (1.4.11). */
export const uiMinimum = 3;

/** Two CSS variables that are seen together, and the ratio they owe each other; a missing scheme means both. */
export interface ContrastPair {
  readonly foreground: string;
  readonly background: string;
  readonly minimum: number;
  readonly scheme?: Scheme;
}

const variable = (name: string): string => `--mantine-color-${name}`;

/**
 * The pairs Mantine actually renders from the tokens, named by variable so the test reads the theme's real output:
 * per accent the text, filled, light and outline variants; then the neutrals that carry the work area.
 */
export function contrastPairs(): readonly ContrastPair[] {
  const pairs: ContrastPair[] = [];
  for (const name of accentNames) {
    pairs.push(
      { foreground: variable(`${name}-text`), background: variable("body"), minimum: textMinimum },
      { foreground: variable("white"), background: variable(`${name}-filled`), minimum: textMinimum },
      { foreground: variable("white"), background: variable(`${name}-filled-hover`), minimum: textMinimum },
      { foreground: variable(`${name}-light-color`), background: variable(`${name}-light`), minimum: textMinimum },
      {
        foreground: variable(`${name}-light-color`),
        background: variable(`${name}-light-hover`),
        minimum: textMinimum,
      },
      { foreground: variable(`${name}-outline`), background: variable("body"), minimum: uiMinimum },
    );
  }
  pairs.push(
    { foreground: variable("text"), background: variable("body"), minimum: textMinimum },
    { foreground: variable("text"), background: variable("default"), minimum: textMinimum },
    { foreground: variable("text"), background: variable("default-hover"), minimum: textMinimum },
    { foreground: variable("dimmed"), background: variable("body"), minimum: textMinimum },
    { foreground: variable("dimmed"), background: variable("default-hover"), minimum: textMinimum },
    { foreground: variable("placeholder"), background: variable("default"), minimum: textMinimum },
    { foreground: variable("error"), background: variable("body"), minimum: textMinimum },
    { foreground: variable("anchor"), background: variable("body"), minimum: textMinimum },
    { foreground: variable("default-color"), background: variable("default"), minimum: textMinimum },
    { foreground: variable("default-border"), background: variable("body"), minimum: uiMinimum },
    { foreground: "--mantine-primary-color-filled", background: variable("body"), minimum: uiMinimum },
    { foreground: variable("black"), background: variable("gray-1"), minimum: textMinimum, scheme: "light" },
    { foreground: variable("white"), background: variable("dark-5"), minimum: textMinimum, scheme: "dark" },
    // A menu item under the pointer or the focus: Mantine's dark default (dark-4) falls just short, the theme sets it.
    { foreground: variable("text"), background: "--menu-item-hover", minimum: textMinimum },
  );
  return pairs;
}
