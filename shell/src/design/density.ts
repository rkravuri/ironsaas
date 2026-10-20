import { useSyncExternalStore } from "react";

export const densities = ["comfortable", "compact"] as const;
export type Density = (typeof densities)[number];
export const defaultDensity: Density = "comfortable";
/** A per-viewer convenience, so it lives in the browser and not on the principal (ADR-0025). */
export const densityStorageKey = "iron.density";

const listeners = new Set<() => void>();
let current: Density | undefined;

const isDensity = (value: unknown): value is Density =>
  typeof value === "string" && (densities as readonly string[]).includes(value);

/** A blocked storage (a private window, cleared site data) means no stored choice, nothing worse. */
function storedDensity(): string | null {
  try {
    return window.localStorage.getItem(densityStorageKey);
  } catch {
    return null;
  }
}

/** The viewer's density: the stored choice, read once per page view, else comfortable. */
export function readDensity(): Density {
  if (current === undefined) {
    const stored = storedDensity();
    current = isDensity(stored) ? stored : defaultDensity;
  }
  return current;
}

/** Puts the density on the root element, where the grid, the record pages and the gallery read it. */
export function applyDensity(density: Density): void {
  document.documentElement.dataset.density = density;
}

export function setDensity(next: Density): void {
  current = next;
  try {
    window.localStorage.setItem(densityStorageKey, next);
  } catch {
    // Storage may refuse; the choice still holds for this page view.
  }
  applyDensity(next);
  for (const listener of listeners) {
    listener();
  }
}

export function subscribeDensity(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** The density, re-rendering whoever reads it when it changes. */
export function useDensity(): Density {
  return useSyncExternalStore(subscribeDensity, readDensity, () => defaultDensity);
}
