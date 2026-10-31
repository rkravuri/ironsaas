import { describe, expect, it } from "vitest";
import { manifests } from "./index";
import type { ModuleManifest } from "./registry";

/**
 * Every module's manifest file, by its path under src/modules, read at transform time (ADR-0029, ADR-0043). The call
 * has no type argument, so Vitest sees it and never caches this file's transform with a stale list of folders.
 */
const manifestFiles: Record<string, Record<string, unknown>> = import.meta.glob(
  ["./kernel/*/manifest.tsx", "./business/*/manifest.tsx", "./suites/*/*/manifest.tsx"],
  { eager: true },
);

const isManifest = (value: unknown): value is ModuleManifest =>
  typeof value === "object" && value !== null && "name" in value && "routes" in value && "navigation" in value;

/** What is wrong with where a manifest sits: a suite's module declares its folder's suite, a Foundation module none. */
function placementProblems(path: string, manifest: Pick<ModuleManifest, "name" | "suite">): string[] {
  const folder = /^[.]\/suites\/([^/]+)\/[^/]+\/manifest[.]tsx$/u.exec(path)?.[1];
  if (folder === undefined) {
    return manifest.suite === undefined
      ? []
      : [
          `${path}: the ${manifest.name} module is the Foundation's and declares no suite, but says "${manifest.suite}".`,
        ];
  }
  return manifest.suite === folder
    ? []
    : [`${path}: the ${manifest.name} module sits in suites/${folder}/ and must declare suite: "${folder}".`];
}

describe("where a module's manifest sits (ADR-0043)", () => {
  it("holds a suite module's declared suite to its folder, and a Foundation module to none [ADR-0043]", () => {
    expect(
      placementProblems("./suites/procurement/orders/manifest.tsx", { name: "orders", suite: "procurement" }),
    ).toEqual([]);
    expect(placementProblems("./kernel/identity/manifest.tsx", { name: "identity" })).toEqual([]);
    expect(placementProblems("./business/items/manifest.tsx", { name: "items" })).toEqual([]);

    expect(placementProblems("./suites/procurement/orders/manifest.tsx", { name: "orders" })).toEqual([
      './suites/procurement/orders/manifest.tsx: the orders module sits in suites/procurement/ and must declare suite: "procurement".',
    ]);
    expect(
      placementProblems("./suites/procurement/orders/manifest.tsx", { name: "orders", suite: "school" }),
    ).toHaveLength(1);
    expect(placementProblems("./kernel/identity/manifest.tsx", { name: "identity", suite: "procurement" })).toEqual([
      './kernel/identity/manifest.tsx: the identity module is the Foundation\'s and declares no suite, but says "procurement".',
    ]);
  });

  it("finds every manifest in its place, and every one of them in the module list [ADR-0043]", () => {
    const found = Object.entries(manifestFiles).flatMap(([path, exports]) =>
      Object.values(exports)
        .filter(isManifest)
        .map((manifest) => ({ path, manifest })),
    );

    expect(found.length).toBeGreaterThan(0);
    expect(found.flatMap(({ path, manifest }) => placementProblems(path, manifest))).toEqual([]);
    expect(found.map(({ manifest }) => manifest.name).sort()).toEqual(
      manifests.map((manifest) => manifest.name).sort(),
    );
  });
});
