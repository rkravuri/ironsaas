/** The one hand every drawing is made in (brief section 3): the numbers behind the Drawing wrapper and its helpers. */
export const hand = {
  /** The stroke of every outline, in viewBox units; the viewBox is drawn at 1 unit per pixel at the md size. */
  stroke: 2,
  /** The thinner stroke, for detail only. */
  detail: 1,
  ink: "var(--iron-ink)",
  accent: "var(--iron-accent-primary)",
  /** A flat fill in the accent is this faint, so it reads as a tint on both papers. */
  fillOpacity: 0.16,
  viewBox: "0 0 160 120",
  width: 160,
  height: 120,
} as const;

/** The three widths a page may use, so drawings sit consistently: sm in a grid or a loader, md on a page, lg in the gallery. */
export const drawingSizes = { sm: 120, md: 200, lg: 280 } as const;
export type DrawingSize = keyof typeof drawingSizes;
