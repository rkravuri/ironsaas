import { seedFrom, symmetries, type MandalaParameters } from "./mandala";

/**
 * A unit's mark from its code, unique in the tenant (ADR-0038): no configuration, and each unit has its own. Two or
 * three rings where the tenant's mark has three to five, so a unit's never reads as the tenant's beside it in the header.
 */
export function unitMarkParameters(code: string): MandalaParameters {
  const seed = `unit:${code.trim().toLowerCase()}`;
  const hash = seedFrom(seed);
  return {
    symmetry: symmetries[hash % symmetries.length] ?? 8,
    rings: 2 + ((hash >>> 3) % 2),
    stroke: 2,
    density: 0.45 + ((hash >>> 6) % 40) / 100,
    seed,
  };
}
