import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import en from "@/i18n/en.json";
import type { IllustrationProps } from "./Drawing";
import { drawingSizes, hand } from "./hand";
import { illustrationNames } from "./names";
import { illustrations } from "./catalogue";

const mount = (name: (typeof illustrationNames)[number], props: IllustrationProps = {}) => {
  const Illustration = illustrations[name];
  const { container, unmount } = render(<Illustration {...props} />);
  const svg = container.querySelector("svg");
  if (!svg) {
    throw new Error(`${name} rendered no svg`);
  }
  return { svg, unmount };
};

describe("the illustrations", () => {
  it("each has an accessible name from the catalogue", () => {
    for (const name of illustrationNames) {
      const { svg, unmount } = mount(name);
      expect(svg).toHaveAttribute("role", "img");
      expect(svg).toHaveAttribute("aria-label", en.illustrations[name]);
      expect(svg).toHaveAttribute("data-drawing", name);
      expect(svg.getAttribute("aria-hidden")).toBeNull();
      unmount();
    }
  });

  it("each can be decorative, hidden from assistive technology", () => {
    for (const name of illustrationNames) {
      const { svg, unmount } = mount(name, { decorative: true });
      expect(svg).toHaveAttribute("aria-hidden", "true");
      expect(svg.getAttribute("role")).toBeNull();
      expect(svg.getAttribute("aria-label")).toBeNull();
      unmount();
    }
  });

  it("comes in three named sizes that keep the box's proportions", () => {
    const { svg: small, unmount: unmountSmall } = mount("welcome", { size: "sm" });
    expect(small).toHaveAttribute("width", String(drawingSizes.sm));
    expect(small).toHaveAttribute("height", "90");
    unmountSmall();
    const { svg: large, unmount: unmountLarge } = mount("welcome", { size: "lg" });
    expect(large).toHaveAttribute("width", String(drawingSizes.lg));
    expect(large).toHaveAttribute("height", "210");
    unmountLarge();
    const { svg: medium, unmount: unmountMedium } = mount("welcome");
    expect(medium).toHaveAttribute("width", String(drawingSizes.md));
    unmountMedium();
  });

  it("is drawn in one hand: the stroke, the caps, the joins, and tints as the only fills", () => {
    for (const name of illustrationNames) {
      const { svg, unmount } = mount(name);
      expect(svg).toHaveAttribute("stroke-width", String(hand.stroke));
      expect(svg).toHaveAttribute("stroke-linecap", "round");
      expect(svg).toHaveAttribute("stroke-linejoin", "round");
      expect(svg).toHaveAttribute("fill", "none");
      const filled = [...svg.querySelectorAll("[fill]")].filter((element) => element.getAttribute("fill") !== "none");
      expect(filled.length).toBeLessThanOrEqual(2);
      for (const element of filled) {
        expect(element.getAttribute("fill-opacity")).toBe(String(hand.fillOpacity));
      }
      const widths = new Set([...svg.querySelectorAll("[stroke-width]")].map((element) => element.getAttribute("stroke-width")));
      for (const width of widths) {
        expect([String(hand.detail), String(hand.stroke)]).toContain(width);
      }
      unmount();
    }
  });
});
