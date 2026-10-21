import { renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { reducedMotionQuery, usePrefersReducedMotion } from "./motion";

const mediaQueryList = (matches: boolean) => (query: string) =>
  ({
    matches: matches && query === reducedMotionQuery,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: () => false,
  }) as unknown as MediaQueryList;

afterEach(() => {
  vi.restoreAllMocks();
});

describe("usePrefersReducedMotion", () => {
  it("is false while the viewer has not asked for less motion [ADR-0025]", () => {
    expect(renderHook(() => usePrefersReducedMotion()).result.current).toBe(false);
  });

  it("is true once the viewer has [ADR-0025]", () => {
    vi.spyOn(window, "matchMedia").mockImplementation(mediaQueryList(true));
    expect(renderHook(() => usePrefersReducedMotion()).result.current).toBe(true);
  });
});
