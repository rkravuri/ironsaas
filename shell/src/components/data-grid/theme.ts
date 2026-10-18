import { themeQuartz } from "ag-grid-community";

/**
 * The grid in Mantine's clothes: every colour and size is a Mantine CSS variable, so the grid follows the theme and
 * the colour scheme without a second set of tokens (S1 plan 3.10: theming tokens).
 */
export const dataGridTheme = themeQuartz.withParams({
  accentColor: "var(--mantine-primary-color-filled)",
  backgroundColor: "var(--mantine-color-body)",
  foregroundColor: "var(--mantine-color-text)",
  // The pager and other subtle text: Mantine mixes the foreground at half, which falls under 4.5:1.
  subtleTextColor: "var(--mantine-color-dimmed)",
  borderColor: "var(--mantine-color-default-border)",
  headerBackgroundColor: "var(--mantine-color-default-hover)",
  headerTextColor: "var(--mantine-color-text)",
  rowHoverColor: "var(--mantine-color-default-hover)",
  selectedRowBackgroundColor: "var(--mantine-primary-color-light)",
  oddRowBackgroundColor: "var(--mantine-color-body)",
  fontFamily: "var(--mantine-font-family)",
  fontSize: "var(--mantine-font-size-sm)",
  headerFontWeight: 600,
  borderRadius: 4,
  wrapperBorderRadius: 8,
  spacing: 6,
});
