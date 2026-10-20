import { describe, expect, it } from "vitest";
import { parseColour, relativeLuminance } from "./contrast";
import { accentShades, oklchToHex, shadesFor } from "./colour";

describe("the OKLCH conversion", () => {
  it("gives the known greys [ADR-0025]", () => {
    expect(oklchToHex({ lightness: 0.5, chroma: 0, hue: 0 })).toBe("#636363");
    expect(oklchToHex({ lightness: 1, chroma: 0, hue: 0 })).toBe("#ffffff");
    expect(oklchToHex({ lightness: 0, chroma: 0, hue: 0 })).toBe("#000000");
  });

  it("keeps the lightness of a colour outside the gamut and gives up chroma instead [ADR-0025]", () => {
    const wanted = 0.6;
    const inside = oklchToHex({ lightness: wanted, chroma: 0, hue: 150 });
    const outside = oklchToHex({ lightness: wanted, chroma: 0.5, hue: 150 });
    const luminance = (hex: string) => relativeLuminance(parseColour(hex));
    expect(Math.abs(luminance(outside) - luminance(inside))).toBeLessThan(0.02);
    expect(outside).not.toBe(inside);
  });
});

describe("a colour scale", () => {
  it("has ten shades that get darker one by one [ADR-0025]", () => {
    const shades = accentShades(255, 0.14);
    expect(shades).toHaveLength(10);
    const luminances = shades.map((shade) => relativeLuminance(parseColour(shade)));
    for (let index = 1; index < luminances.length; index += 1) {
      expect(luminances[index]).toBeLessThan(luminances[index - 1] ?? 0);
    }
  });

  it("refuses a table that is not ten steps long [ADR-0025]", () => {
    expect(() => shadesFor(0, 0, { steps: [0.5, 0.4] })).toThrow("ten steps");
  });
});
