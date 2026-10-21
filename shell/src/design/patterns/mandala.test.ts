import { describe, expect, it } from "vitest";
import { mandala, seedFrom, symmetries } from "./mandala";

const base = { symmetry: 8 as const, rings: 4, stroke: 2, density: 0.7 };
const paths = (seed: string, extra: Partial<typeof base> = {}) =>
  mandala({ ...base, ...extra, seed }).rings.map((ring) => ring.path);

describe("the mandala generator", () => {
  it("gives the same drawing for the same seed, and the same object back [ADR-0026]", () => {
    const first = mandala({ ...base, seed: "acme.localhost" });
    expect(mandala({ ...base, seed: "acme.localhost" })).toBe(first);
    expect(mandala({ seed: "acme.localhost", density: 0.7, stroke: 2, rings: 4, symmetry: 8 })).toBe(first);
    expect(paths("acme.localhost")).toEqual(paths("acme.localhost"));
  });

  it("gives a different drawing for a different seed [ADR-0026]", () => {
    expect(paths("bravo.localhost")).not.toEqual(paths("acme.localhost"));
    expect(paths("acme.localhost", { density: 0.2 })).not.toEqual(paths("acme.localhost"));
  });

  it("repeats every motif as many times as the symmetry says [ADR-0026]", () => {
    for (const symmetry of symmetries) {
      const drawing = mandala({ ...base, symmetry, density: 0.2, seed: `symmetry-${symmetry}` });
      expect(drawing.rings).toHaveLength(base.rings);
      for (const ring of drawing.rings) {
        const repeats = ring.path.split("M").length - 1;
        expect(repeats % symmetry).toBe(0);
        expect(repeats).toBeGreaterThan(0);
      }
    }
  });

  it("marks exactly one ring with the accent and keeps the rings inside the box [ADR-0026]", () => {
    const drawing = mandala({ ...base, seed: "one accent" });
    expect(drawing.rings.filter((ring) => ring.accent)).toHaveLength(1);
    expect(drawing.viewBox).toBe("-50 -50 100 100");
    for (const ring of drawing.rings) {
      expect(ring.radius).toBeGreaterThanOrEqual(12);
      expect(ring.radius).toBeLessThanOrEqual(46);
    }
  });

  it("refuses parameters outside the brief [ADR-0026]", () => {
    expect(() => mandala({ ...base, symmetry: 5 as unknown as 8, seed: "x" })).toThrow("6, 8 or 12");
    expect(() => mandala({ ...base, rings: 7, seed: "x" })).toThrow("2 to 6 rings");
    expect(() => mandala({ ...base, density: 2, seed: "x" })).toThrow("between 0 and 1");
  });

  it("hashes text stably [ADR-0026]", () => {
    expect(seedFrom("acme.localhost")).toBe(seedFrom("acme.localhost"));
    expect(seedFrom("acme.localhost")).not.toBe(seedFrom("bravo.localhost"));
  });
});
