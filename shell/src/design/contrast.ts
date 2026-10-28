/**
 * WCAG 2.1 contrast (success criteria 1.4.3 and 1.4.11) for the tokens test, the gallery and, later, the tenant
 * colour check of TF-173. Accepts the two forms Mantine emits: hex and rgb()/rgba().
 */

export interface Rgba {
  r: number;
  g: number;
  b: number;
  /** 0 to 1. */
  a: number;
}

const hexPattern = /^#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/;
// The function's arguments are matched as one run and split, not by one pattern with a repetition inside an optional
// group, whose backtracking grows with the length of a run of spaces (detect-unsafe-regex, ADR-0055).
const functionPattern = /^rgba?\(([^()]*)\)$/;
const argumentSeparator = /[\s,/]+/;
const channelPattern = /^[\d.]+$/;
const alphaPattern = /^[\d.]+%?$/;

function channels(text: string): [string, string, string, string | undefined] | undefined {
  const parts = text.split(argumentSeparator).filter((part) => part !== "");
  const [r, g, b, alpha] = parts;
  if (parts.length < 3 || parts.length > 4 || r === undefined || g === undefined || b === undefined) {
    return undefined;
  }
  if (![r, g, b].every((part) => channelPattern.test(part)) || (alpha !== undefined && !alphaPattern.test(alpha))) {
    return undefined;
  }
  return [r, g, b, alpha];
}

/** Parses a hex (3, 4, 6 or 8 digits) or an rgb()/rgba() value; anything else is a mistake worth throwing for. */
export function parseColour(value: string): Rgba {
  const text = value.trim().toLowerCase();
  const hex = hexPattern.exec(text);
  if (hex?.[1] !== undefined) {
    const digits = hex[1].length <= 4 ? [...hex[1]].map((digit) => digit + digit).join("") : hex[1];
    const byte = (offset: number): number => parseInt(digits.slice(offset, offset + 2), 16);
    return { r: byte(0), g: byte(2), b: byte(4), a: digits.length === 8 ? byte(6) / 255 : 1 };
  }
  const fn = functionPattern.exec(text);
  const parts = fn?.[1] === undefined ? undefined : channels(fn[1]);
  if (parts !== undefined) {
    const [r, g, b, alphaText] = parts;
    const alpha =
      alphaText === undefined ? 1 : alphaText.endsWith("%") ? Number(alphaText.slice(0, -1)) / 100 : Number(alphaText);
    return { r: Number(r), g: Number(g), b: Number(b), a: alpha };
  }
  throw new Error(`Not a colour value: ${value}`);
}

const linear = (byte: number): number => {
  const channel = byte / 255;
  return channel <= 0.04045 ? channel / 12.92 : Math.pow((channel + 0.055) / 1.055, 2.4);
};

/** Relative luminance as WCAG defines it, 0 for black to 1 for white. */
export function relativeLuminance(colour: Rgba): number {
  return 0.2126 * linear(colour.r) + 0.7152 * linear(colour.g) + 0.0722 * linear(colour.b);
}

/** A translucent foreground is seen over its background, so that is what gets measured. */
function composite(front: Rgba, back: Rgba): Rgba {
  if (front.a >= 1) {
    return front;
  }
  const mix = (f: number, b: number): number => f * front.a + b * (1 - front.a);
  return { r: mix(front.r, back.r), g: mix(front.g, back.g), b: mix(front.b, back.b), a: 1 };
}

/** The contrast ratio between a text or stroke colour and the opaque surface behind it, 1 to 21. */
export function contrastRatio(foreground: string, background: string): number {
  const back = parseColour(background);
  const front = composite(parseColour(foreground), back);
  const first = relativeLuminance(front);
  const second = relativeLuminance(back);
  const lighter = Math.max(first, second);
  const darker = Math.min(first, second);
  return (lighter + 0.05) / (darker + 0.05);
}
