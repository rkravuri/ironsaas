import type { MandalaParameters } from "./mandala";

/** One family for every module: the console's parameters since A1, and the module's name as the seed (ADR-0033). */
export const moduleMarkParameters = (name: string): MandalaParameters => ({
  symmetry: 8,
  rings: 4,
  stroke: 2,
  density: 0.6,
  seed: name,
});
