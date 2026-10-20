import { describe, expect, it } from "vitest";
import en from "@/i18n/en.json";
import { resolveCssVariables } from "@/theme";
import { contrastRatio } from "./contrast";
import { accentNames, contrastPairs, tokens, type Scheme } from "./tokens";

const schemes: Scheme[] = ["light", "dark"];
const pure = ["#000000", "#ffffff"];

describe("the tokens", () => {
  it("meet every contrast pair Mantine renders from them, in both schemes", () => {
    const resolved = resolveCssVariables();
    const failures: string[] = [];
    for (const scheme of schemes) {
      for (const pair of contrastPairs()) {
        if (pair.scheme !== undefined && pair.scheme !== scheme) {
          continue;
        }
        const foreground = resolved[scheme][pair.foreground];
        const background = resolved[scheme][pair.background];
        if (foreground === undefined || background === undefined) {
          failures.push(`${scheme}: ${pair.foreground} or ${pair.background} is not emitted`);
          continue;
        }
        const ratio = contrastRatio(foreground, background);
        if (ratio < pair.minimum) {
          failures.push(`${scheme}: ${pair.foreground} (${foreground}) on ${pair.background} (${background}) is ${ratio.toFixed(2)}:1, below ${pair.minimum}:1`);
        }
      }
    }
    expect(failures).toEqual([]);
  });

  it("use no pure black or white anywhere", () => {
    const values = [
      tokens.ink.light,
      tokens.ink.dark,
      tokens.paper.light,
      tokens.paper.dark,
      ...tokens.neutral.gray,
      ...tokens.neutral.dark,
      ...accentNames.flatMap((name) => [...tokens.accents[name].shades]),
    ];
    expect(values.filter((value) => pure.includes(value))).toEqual([]);
  });

  it("name a meaning for every accent in the catalogue", () => {
    const meanings = en.design.accents as Record<string, string>;
    for (const name of accentNames) {
      expect(tokens.accents[name].meaningKey).toBe(`design.accents.${name}`);
      expect(meanings[name]?.length ?? 0).toBeGreaterThan(10);
    }
  });

  it("have six type sizes from 12 to 32 pixels, the display sizes at 24 and above", () => {
    const sizes = Object.values(tokens.type.sizes).map((size) => size.px);
    expect(sizes).toEqual([12, 14, 16, 20, 24, 32]);
    expect(tokens.type.sizes.xl.px).toBeGreaterThanOrEqual(24);
  });

  it("keep the motion short", () => {
    expect(tokens.motion.duration.quick).toBe(150);
    expect(tokens.motion.duration.calm).toBe(250);
  });
});
