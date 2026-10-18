import { createTheme } from "@mantine/core";

/** The one theme every tenant gets in S1; tenant theming tokens join with the design system work. */
export const theme = createTheme({
  primaryColor: "indigo",
  defaultRadius: "md",
  fontFamily: "Inter, system-ui, -apple-system, Segoe UI, Roboto, sans-serif",
  headings: { fontWeight: "600" },
});
