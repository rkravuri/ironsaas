import { render } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Mandala } from "./Mandala";
import { tenantMarkParameters } from "./tenant-mark";
import { TenantMark } from "./TenantMark";
import { reducedMotionQuery } from "../motion";

const pathsOf = (hostname: string): (string | null)[] => {
  const { container, unmount } = render(<TenantMark hostname={hostname} size={48} />);
  const paths = [...container.querySelectorAll("path")].map((path) => path.getAttribute("d"));
  unmount();
  return paths;
};

afterEach(() => {
  vi.restoreAllMocks();
});

describe("the tenant mark", () => {
  it("differs between acme.localhost and bravo.localhost, and ignores case", () => {
    expect(pathsOf("acme.localhost")).not.toEqual(pathsOf("bravo.localhost"));
    expect(pathsOf("acme.localhost")).toEqual(pathsOf("ACME.localhost"));
  });

  it("is hidden from assistive technology and carries its seed", () => {
    const { container } = render(<TenantMark hostname="acme.localhost" size={24} />);
    const svg = container.querySelector("svg");
    expect(svg).toHaveAttribute("aria-hidden", "true");
    expect(svg).toHaveAttribute("data-mandala", "acme.localhost");
    expect(svg).toHaveAttribute("width", "24");
  });

  it("stays within the brief's parameters for any hostname", () => {
    for (const hostname of ["a", "smoke.localhost", "a-tenant-with-a-long-name.example.com"]) {
      const parameters = tenantMarkParameters(hostname);
      expect([6, 8, 12]).toContain(parameters.symmetry);
      expect(parameters.rings).toBeGreaterThanOrEqual(3);
      expect(parameters.rings).toBeLessThanOrEqual(5);
      expect(parameters.density).toBeGreaterThanOrEqual(0.5);
      expect(parameters.density).toBeLessThanOrEqual(1);
    }
  });
});

describe("the mandala's draw-in", () => {
  it("animates when asked and the viewer allows motion", () => {
    const { container } = render(<Mandala symmetry={6} rings={3} stroke={2} density={0.5} seed="draw" size={40} animate />);
    expect(container.querySelector("path.iron-draw-in")).not.toBeNull();
  });

  it("stands still when the viewer asked for less motion", () => {
    vi.spyOn(window, "matchMedia").mockImplementation(
      (query: string) =>
        ({ matches: query === reducedMotionQuery, media: query, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: () => false }),
    );
    const { container } = render(<Mandala symmetry={6} rings={3} stroke={2} density={0.5} seed="still" size={40} animate />);
    expect(container.querySelector("path.iron-draw-in")).toBeNull();
  });
});
