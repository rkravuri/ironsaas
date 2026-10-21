import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { densityStorageKey, readDensity, setDensity, useDensity } from "./density";

afterEach(() => {
  vi.restoreAllMocks();
  act(() => {
    setDensity("comfortable");
  });
  window.localStorage.clear();
});

describe("the density setting", () => {
  it("is comfortable until the viewer chooses [ADR-0024, ADR-0025]", () => {
    expect(readDensity()).toBe("comfortable");
    const { result } = renderHook(() => useDensity());
    expect(result.current).toBe("comfortable");
  });

  it("tells every reader, the root element and the storage about a change [ADR-0025]", () => {
    const { result } = renderHook(() => useDensity());
    act(() => {
      setDensity("compact");
    });
    expect(result.current).toBe("compact");
    expect(document.documentElement.dataset.density).toBe("compact");
    expect(window.localStorage.getItem(densityStorageKey)).toBe("compact");
  });

  it("still applies when the storage refuses [ADR-0025]", () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    const { result } = renderHook(() => useDensity());
    act(() => {
      setDensity("compact");
    });
    expect(result.current).toBe("compact");
    expect(document.documentElement.dataset.density).toBe("compact");
  });
});
