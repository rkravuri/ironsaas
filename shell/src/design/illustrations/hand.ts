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

/**
 * The only way to draw a person: a round head with a cap line and no face, a closed rounded torso in the accent tint,
 * arms and legs that bend once at the elbow and the knee, short strokes for feet. The same proportions every time;
 * the exaggeration is in the posture and the objects, never in features (brief section 3).
 */
export type FigurePose = "reading" | "waving" | "holding";

export interface FigureParts {
  head: { cx: number; cy: number; r: number };
  cap: string;
  neck: string;
  torso: string;
  arms: readonly string[];
  legs: readonly string[];
  feet: readonly string[];
}

type Point = readonly [number, number];
const line = (points: readonly Point[]): string => points.map(([x, y], index) => `${index === 0 ? "M" : "L"}${x} ${y}`).join(" ");

/** Shoulder, elbow, hand; hip, knee, foot: the joints of each pose relative to the centre of the head. */
const poses: Record<FigurePose, { arms: readonly (readonly Point[])[]; legs: readonly (readonly Point[])[] }> = {
  // Both hands together at the chest, the elbows out: reading what is held.
  reading: {
    arms: [
      [[-7, 13], [-13, 22], [-6, 28]],
      [[7, 13], [13, 22], [6, 28]],
    ],
    legs: [
      [[-4, 34], [-5, 47], [-6, 60]],
      [[4, 34], [5, 47], [6, 60]],
    ],
  },
  // One arm up with the hand above the head, the other at ease; a knee a little bent.
  waving: {
    arms: [
      [[-7, 13], [-11, 24], [-8, 34]],
      [[7, 13], [15, 8], [13, -4]],
    ],
    legs: [
      [[-4, 34], [-4, 47], [-5, 60]],
      [[4, 34], [7, 47], [9, 60]],
    ],
  },
  // Both hands above the head, the elbows out; the feet planted apart.
  holding: {
    arms: [
      [[-7, 13], [-14, 4], [-9, -8]],
      [[7, 13], [14, 4], [9, -8]],
    ],
    legs: [
      [[-4, 34], [-8, 47], [-10, 60]],
      [[4, 34], [8, 47], [10, 60]],
    ],
  },
};

/** The parts of a person whose head is centred at (x, y); the feet land 60 units below the head's centre. */
export function figure(x: number, y: number, pose: FigurePose): FigureParts {
  const at = (points: readonly Point[]): readonly Point[] => points.map(([dx, dy]) => [x + dx, y + dy] as const);
  const { arms, legs } = poses[pose];
  return {
    head: { cx: x, cy: y, r: 7 },
    cap: `M${x - 5} ${y - 4} q5 -4 10 0`,
    neck: `M${x} ${y + 7} V${y + 10}`,
    torso: `M${x - 3} ${y + 10} h6 a4 4 0 0 1 4 4 v16 a4 4 0 0 1 -4 4 h-6 a4 4 0 0 1 -4 -4 v-16 a4 4 0 0 1 4 -4 z`,
    arms: arms.map((arm) => line(at(arm))),
    legs: legs.map((leg) => line(at(leg))),
    feet: legs.map((leg, index) => {
      const [footX, footY] = at(leg)[2] ?? [x, y + 60];
      return `M${footX - (index === 0 ? 4 : 3)} ${footY} h7`;
    }),
  };
}
