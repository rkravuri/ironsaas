import { describe, expect, it } from "vitest";
import { contrastRatio, parseColour } from "./contrast";

describe("the contrast ratio", () => {
  it("is 21 for black on white and 1 for a colour on itself", () => {
    expect(contrastRatio("#000000", "#ffffff")).toBeCloseTo(21, 5);
    expect(contrastRatio("#336699", "#336699")).toBeCloseTo(1, 5);
  });

  it("matches the WCAG example of grey on white", () => {
    expect(contrastRatio("#777777", "#ffffff")).toBeCloseTo(4.48, 2);
  });

  it("is the same whichever colour is the text", () => {
    expect(contrastRatio("#1f2430", "#faf8f3")).toBeCloseTo(contrastRatio("#faf8f3", "#1f2430"), 10);
  });

  it("sees a translucent colour over its background", () => {
    expect(contrastRatio("rgba(0, 0, 0, 0.5)", "#ffffff")).toBeLessThan(contrastRatio("#000000", "#ffffff"));
    expect(contrastRatio("rgba(0, 0, 0, 0)", "#ffffff")).toBeCloseTo(1, 5);
  });

  it("reads the forms Mantine emits", () => {
    expect(parseColour("#abc")).toEqual({ r: 170, g: 187, b: 204, a: 1 });
    expect(parseColour("#aabbcc80").a).toBeCloseTo(0.5, 1);
    expect(parseColour("rgba(12, 34, 56, 1)")).toEqual({ r: 12, g: 34, b: 56, a: 1 });
    expect(parseColour("rgb(1 2 3 / 50%)")).toEqual({ r: 1, g: 2, b: 3, a: 0.5 });
    expect(() => parseColour("var(--mantine-color-text)")).toThrow("Not a colour value");
  });
});
