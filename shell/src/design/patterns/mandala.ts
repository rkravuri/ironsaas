/**
 * Ornament from geometry (ADR-0024 item 3, brief section 4): a mandala from a few parameters and a seed. The
 * function is pure and deterministic, so a tenant's mark is the same on every page and every machine, and its
 * output is memoised so a re-render never recomputes the paths (ADR-0026).
 */

export type Symmetry = 6 | 8 | 12;

export interface MandalaParameters {
  /** How many times each motif repeats around the centre. */
  symmetry: Symmetry;
  /** Concentric rings, 2 to 6. */
  rings: number;
  /** Stroke width of the ordinary rings in viewBox units; detail rings use half of it. */
  stroke: number;
  /** 0 to 1: how much happens on each ring. */
  density: number;
  /** Any text or number; the same seed gives the same drawing. */
  seed: string | number;
}

export interface MandalaRing {
  radius: number;
  /** SVG path data of the whole ring, every repeat included, so the data stands on its own. */
  path: string;
  /** Drawn in the accent rather than the ink. */
  accent: boolean;
  strokeWidth: number;
}

export interface MandalaDrawing {
  viewBox: string;
  rings: readonly MandalaRing[];
}

export const symmetries: readonly Symmetry[] = [6, 8, 12];
export const mandalaViewBox = "-50 -50 100 100";

/** FNV-1a over the UTF-16 code units: a stable 32-bit number from any text. */
export function seedFrom(text: string): number {
  let hash = 0x811c9dc5;
  for (let index = 0; index < text.length; index += 1) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash >>> 0;
}

/** mulberry32: a small, fast stream of numbers in [0, 1) from one 32-bit seed. */
export function stream(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const round = (value: number): number => Math.round(value * 100) / 100;
const point = (radius: number, angle: number): string =>
  `${round(radius * Math.cos(angle))} ${round(radius * Math.sin(angle))}`;

/** One repeat of a motif at a ring: the path for the wedge that starts at the angle. */
type Motif = (radius: number, angle: number, halfWidth: number, height: number) => string;

const motifs: readonly Motif[] = [
  // A petal: a curve that bulges outward.
  (radius, angle, halfWidth, height) =>
    `M${point(radius, angle - halfWidth)} Q${point(radius + height, angle)} ${point(radius, angle + halfWidth)}`,
  // A chevron pointing outward.
  (radius, angle, halfWidth, height) =>
    `M${point(radius - height, angle - halfWidth)} L${point(radius, angle)} L${point(radius - height, angle + halfWidth)}`,
  // A radial tick.
  (radius, angle, _halfWidth, height) => `M${point(radius - height / 2, angle)} L${point(radius + height / 2, angle)}`,
  // A leaf: two curves meeting at a point.
  (radius, angle, halfWidth, height) =>
    `M${point(radius, angle - halfWidth)} Q${point(radius + height, angle - halfWidth / 2)} ${point(radius + height, angle)} Q${point(radius + height, angle + halfWidth / 2)} ${point(radius, angle + halfWidth)}`,
  // A dot, drawn as two arcs so it stays a stroke.
  (radius, angle, _halfWidth, height) => {
    const size = Math.max(0.6, height / 4);
    const [x, y] = [radius * Math.cos(angle), radius * Math.sin(angle)];
    return `M${round(x - size)} ${round(y)} a${size} ${size} 0 1 0 ${round(size * 2)} 0 a${size} ${size} 0 1 0 ${round(-size * 2)} 0`;
  },
];

const circle = (radius: number): string =>
  `M${radius} 0 A${radius} ${radius} 0 1 1 ${-radius} 0 A${radius} ${radius} 0 1 1 ${radius} 0`;

function validate({ symmetry, rings, stroke, density }: MandalaParameters): void {
  if (!symmetries.includes(symmetry)) {
    throw new Error("A mandala's symmetry is 6, 8 or 12.");
  }
  if (!Number.isInteger(rings) || rings < 2 || rings > 6) {
    throw new Error("A mandala has 2 to 6 rings.");
  }
  if (!(stroke > 0) || density < 0 || density > 1) {
    throw new Error("A mandala's stroke is positive and its density is between 0 and 1.");
  }
}

function draw(parameters: MandalaParameters): MandalaDrawing {
  validate(parameters);
  const { symmetry, rings, stroke, density, seed } = parameters;
  const random = stream(seedFrom(String(seed)));
  const accentRing = Math.floor(random() * rings);
  const result: MandalaRing[] = [];
  for (let index = 0; index < rings; index += 1) {
    const radius = round(12 + (index * 34) / (rings - 1));
    const motif = motifs[Math.floor(random() * motifs.length)] ?? motifs[0]!;
    const repeats = density > 0.6 && index % 2 === 1 ? symmetry * 2 : symmetry;
    const halfWidth = ((Math.PI * 2) / repeats) * (0.25 + random() * 0.2);
    const height = round(3 + random() * 6 * (0.4 + density));
    const detail = random() < 0.35;
    const parts: string[] = [];
    if (density > 0.3 && random() < density) {
      parts.push(circle(radius));
    }
    for (let repeat = 0; repeat < repeats; repeat += 1) {
      parts.push(motif(radius, repeat * ((Math.PI * 2) / repeats), halfWidth, height));
    }
    result.push({
      radius,
      path: parts.join(" "),
      accent: index === accentRing,
      strokeWidth: detail ? stroke / 2 : stroke,
    });
  }
  return { viewBox: mandalaViewBox, rings: result };
}

// The same inputs hand back the same object: a mark is drawn on every page, and React keeps the paths stable.
const cacheLimit = 64;
const cache = new Map<string, MandalaDrawing>();

/** The drawing for the parameters: computed once per distinct input, then served from a small cache. */
export function mandala(parameters: MandalaParameters): MandalaDrawing {
  const key = JSON.stringify([
    parameters.symmetry,
    parameters.rings,
    parameters.stroke,
    parameters.density,
    String(parameters.seed),
  ]);
  const cached = cache.get(key);
  if (cached) {
    return cached;
  }
  const drawing = draw(parameters);
  if (cache.size >= cacheLimit) {
    const oldest = cache.keys().next().value;
    if (oldest !== undefined) {
      cache.delete(oldest);
    }
  }
  cache.set(key, drawing);
  return drawing;
}
