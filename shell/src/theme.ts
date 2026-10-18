import { Anchor, createTheme, DEFAULT_THEME, defaultCssVariablesResolver, mergeMantineTheme, rem, Title, type CSSVariablesResolver } from "@mantine/core";
import { accentNames, tokens, type Scheme, type TypeSize } from "@/design/tokens";

/** A heading level in the type scale: the size and its line height as the unitless ratio Mantine wants. */
const heading = (size: TypeSize) => ({ fontSize: rem(tokens.type.sizes[size].px), lineHeight: String(tokens.type.sizes[size].lineHeight / tokens.type.sizes[size].px) });

const sizeKeys = Object.keys(tokens.type.sizes) as TypeSize[];
const fontSizes = Object.fromEntries(sizeKeys.map((size) => [size, rem(tokens.type.sizes[size].px)]));
const lineHeights = Object.fromEntries(sizeKeys.map((size) => [size, String(tokens.type.sizes[size].lineHeight / tokens.type.sizes[size].px)]));
const scale = (values: Record<string, number>) => Object.fromEntries(Object.entries(values).map(([key, value]) => [key, rem(value)]));

/**
 * The theme every tenant gets, built from the tokens (ADR-0025): ink and paper as black and white, the accents by
 * their meaning, one shade for filled surfaces in both schemes (shade 8 would lose 3:1 on the dark paper), the
 * reading family everywhere and the display family only where a title asks for it at 24 px or more.
 */
export const theme = createTheme({
  white: tokens.paper.light,
  black: tokens.ink.light,
  colors: {
    primary: tokens.accents.primary.shades,
    identity: tokens.accents.identity.shades,
    console: tokens.accents.console.shades,
    success: tokens.accents.success.shades,
    warning: tokens.accents.warning.shades,
    danger: tokens.accents.danger.shades,
    info: tokens.accents.info.shades,
    gray: tokens.neutral.gray,
    dark: tokens.neutral.dark,
  },
  primaryColor: "primary",
  primaryShade: 6,
  fontFamily: tokens.type.reading.family,
  fontFamilyMonospace: tokens.type.mono.family,
  headings: {
    fontFamily: tokens.type.reading.family,
    fontWeight: "600",
    sizes: { h1: heading("xxl"), h2: heading("xl"), h3: heading("lg"), h4: heading("md"), h5: heading("sm"), h6: heading("xs") },
  },
  fontSizes,
  lineHeights,
  spacing: scale(tokens.spacing),
  radius: scale(tokens.radius),
  defaultRadius: "md",
  focusRing: "auto",
  respectReducedMotion: true,
  components: {
    // The display family is opt-in and only at 24 px or above: a moment's title says variant="display".
    Title: Title.extend({
      styles: (_theme, props) => (props.variant === "display" && (props.order ?? 1) <= 2 ? { root: { fontFamily: tokens.type.display.family } } : {}),
    }),
    // A link told apart by colour alone fails WCAG 1.4.1.
    Anchor: Anchor.extend({ defaultProps: { underline: "always" } }),
  },
});

/**
 * The tokens as CSS variables, next to Mantine's own: `--iron-*` for what pages, drawings and the grid read, and the
 * few Mantine variables whose defaults name a hue (error, success) or fall short of AA (placeholder).
 */
export const cssVariablesResolver: CSSVariablesResolver = () => ({
  variables: {
    "--iron-font-reading": tokens.type.reading.family,
    "--iron-font-display": tokens.type.display.family,
    "--iron-font-mono": tokens.type.mono.family,
    "--iron-duration-quick": `${tokens.motion.duration.quick}ms`,
    "--iron-duration-calm": `${tokens.motion.duration.calm}ms`,
    "--iron-ease-enter": tokens.motion.easing.enter,
    "--iron-ease-exit": tokens.motion.easing.exit,
    ...Object.fromEntries(accentNames.map((name) => [`--iron-accent-${name}`, `var(--mantine-color-${name}-text)`])),
  },
  light: {
    "--iron-ink": tokens.ink.light,
    "--iron-paper": tokens.paper.light,
    "--mantine-color-error": "var(--mantine-color-danger-6)",
    "--mantine-color-success": "var(--mantine-color-success-6)",
    "--mantine-color-placeholder": "var(--mantine-color-dimmed)",
  },
  dark: {
    "--iron-ink": tokens.ink.dark,
    "--iron-paper": tokens.paper.dark,
    "--mantine-color-error": "var(--mantine-color-danger-4)",
    "--mantine-color-success": "var(--mantine-color-success-4)",
    "--mantine-color-placeholder": "var(--mantine-color-dimmed)",
  },
});

/** Follows var() references until a value remains, so a pair can be measured as the browser would paint it. */
function resolveReferences(variables: Record<string, string>): Record<string, string> {
  const resolve = (value: string, depth: number): string => {
    if (depth > 20) {
      throw new Error(`A CSS variable chain is too deep: ${value}`);
    }
    return value.replace(/var\((--[\w-]+)\)/g, (reference: string, name: string) => {
      const next = variables[name];
      return next === undefined ? reference : resolve(next, depth + 1);
    });
  };
  return Object.fromEntries(Object.entries(variables).map(([name, value]) => [name, resolve(value, 0)]));
}

/** Every CSS variable the provider emits for the theme, per scheme, with references followed to their values. */
export function resolveCssVariables(): Record<Scheme, Record<string, string>> {
  const merged = mergeMantineTheme(DEFAULT_THEME, theme);
  const defaults = defaultCssVariablesResolver(merged);
  const ours = cssVariablesResolver(merged);
  const shared = { ...defaults.variables, ...ours.variables };
  return {
    light: resolveReferences({ ...shared, ...defaults.light, ...ours.light }),
    dark: resolveReferences({ ...shared, ...defaults.dark, ...ours.dark }),
  };
}
