import { seedFrom, symmetries, type MandalaParameters } from "./mandala";

/** What a hostname gives: no configuration, and every tenant has something of its own (brief section 4). */
export function tenantMarkParameters(hostname: string): MandalaParameters {
  const seed = hostname.trim().toLowerCase();
  const hash = seedFrom(seed);
  return {
    symmetry: symmetries[hash % symmetries.length] ?? 8,
    rings: 3 + ((hash >>> 3) % 3),
    stroke: 2,
    density: 0.5 + ((hash >>> 6) % 50) / 100,
    seed,
  };
}
