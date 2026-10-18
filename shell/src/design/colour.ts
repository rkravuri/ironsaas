/**
 * Colour maths for the tokens (ADR-0025): OKLCH in, sRGB hex out. Every accent is one hue and one chroma expanded on
 * one lightness table, so the palette is one family and every scale meets the same contrast pairs by construction.
 * OKLCH lightness tracks the luminance WCAG measures closely across hues, which is why a lightness table can promise
 * contrast; the tokens test checks the promise on the values Mantine renders.
 */

export interface Oklch {
  /** 0 is black, 1 is white. */
  lightness: number;
  /** 0 is grey; around 0.15 is a saturated accent. */
  chroma: number;
  /** Degrees, 0 to 360. */
  hue: number;
}

/** Ten shades, lightest first, in the order Mantine reads a colour tuple. */
export type Shades = readonly [string, string, string, string, string, string, string, string, string, string];

export interface ScaleOptions {
  /** Lightness of each shade, lightest first. */
  steps: readonly number[];
  /** Chroma of each shade as a fraction of the scale's chroma; pale at the ends keeps tints and shades clean. */
  ramp?: readonly number[];
}

/** Accent lightness: shade 6 carries text and filled surfaces in both schemes, shades 1 and 9 the light variant. */
export const accentSteps: readonly number[] = [0.97, 0.93, 0.87, 0.79, 0.7, 0.62, 0.53, 0.46, 0.39, 0.31];

/** Accent chroma per shade: full in the middle, pale at the ends. */
export const chromaRamp: readonly number[] = [0.2, 0.4, 0.6, 0.8, 0.95, 1, 1, 0.9, 0.75, 0.6];

/** The light scheme's neutrals (Mantine's gray): 0 hovers, 4 draws borders at 3:1, 6 is dimmed text at 4.5:1. */
export const graySteps: readonly number[] = [0.97, 0.93, 0.88, 0.8, 0.64, 0.55, 0.46, 0.38, 0.3, 0.22];

/** The dark scheme's neutrals (Mantine's dark): 0 is the ink, 2 dimmed text, 4 borders at 3:1, 7 the paper. */
export const darkSteps: readonly number[] = [0.93, 0.86, 0.74, 0.64, 0.52, 0.3, 0.26, 0.21, 0.17, 0.13];

const flatRamp: readonly number[] = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1];

function toLinearRgb({ lightness, chroma, hue }: Oklch): [number, number, number] {
  const radians = (hue * Math.PI) / 180;
  const a = chroma * Math.cos(radians);
  const b = chroma * Math.sin(radians);
  const l = Math.pow(lightness + 0.3963377774 * a + 0.2158037573 * b, 3);
  const m = Math.pow(lightness - 0.1055613458 * a - 0.0638541728 * b, 3);
  const s = Math.pow(lightness - 0.0894841775 * a - 1.291485548 * b, 3);
  return [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ];
}

// Rounding noise just outside 0 or 1 is not out of gamut; a real excursion is at least a hundredth.
const tolerance = 0.0005;
const inGamut = (rgb: readonly number[]): boolean => rgb.every((channel) => channel >= -tolerance && channel <= 1 + tolerance);

const linearToSrgb = (channel: number): number => (channel <= 0.0031308 ? 12.92 * channel : 1.055 * Math.pow(channel, 1 / 2.4) - 0.055);

const toHexByte = (channel: number): string =>
  Math.round(255 * linearToSrgb(Math.min(1, Math.max(0, channel))))
    .toString(16)
    .padStart(2, "0");

/** The sRGB hex of an OKLCH colour. A colour outside the sRGB gamut gives up chroma, never lightness. */
export function oklchToHex(colour: Oklch): string {
  let chroma = colour.chroma;
  let rgb = toLinearRgb({ ...colour, chroma });
  while (!inGamut(rgb) && chroma > 0) {
    chroma = Math.max(0, chroma - 0.005);
    rgb = toLinearRgb({ ...colour, chroma });
  }
  return `#${rgb.map(toHexByte).join("")}`;
}

/** Ten shades of one hue on a lightness table; the accents and the neutrals all come from here. */
export function shadesFor(hue: number, chroma: number, { steps, ramp = flatRamp }: ScaleOptions): Shades {
  if (steps.length !== 10 || ramp.length !== 10) {
    throw new Error("A colour scale has exactly ten steps.");
  }
  const shades = steps.map((lightness, index) => oklchToHex({ lightness, chroma: chroma * (ramp[index] ?? 1), hue }));
  return shades as unknown as Shades;
}

/** An accent scale: the accent table and the chroma ramp. */
export const accentShades = (hue: number, chroma: number): Shades => shadesFor(hue, chroma, { steps: accentSteps, ramp: chromaRamp });
