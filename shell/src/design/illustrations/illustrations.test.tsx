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
      const widths = new Set(
        [...svg.querySelectorAll("[stroke-width]")].map((element) => element.getAttribute("stroke-width")),
      );
      for (const width of widths) {
        expect([String(hand.detail), String(hand.stroke)]).toContain(width);
      }
      unmount();
    }
  });
});

describe("the figure", () => {
  const people: Record<string, "reading" | "waving" | "holding"> = {
    lost: "reading",
    welcome: "waving",
    success: "holding",
  };

  it("draws every person the same way: a head with a cap line and no face, a tinted torso, limbs that bend, feet", () => {
    for (const [name, pose] of Object.entries(people)) {
      const { svg, unmount } = mount(name as (typeof illustrationNames)[number]);
      const group = svg.querySelector(`[data-figure='${pose}']`);
      expect(group).not.toBeNull();
      expect(group?.querySelectorAll("circle")).toHaveLength(1);
      const paths = [...(group?.querySelectorAll("path") ?? [])];
      expect(paths).toHaveLength(9);
      expect(paths.filter((path) => path.getAttribute("fill-opacity") === String(hand.fillOpacity))).toHaveLength(1);
      const limbs = paths.map((path) => path.getAttribute("d") ?? "").filter((d) => d.split("L").length === 3);
      expect(limbs).toHaveLength(4);
      unmount();
    }
  });

  it("changes the posture, not the person, between the three drawings", () => {
    const limbsOf = (name: (typeof illustrationNames)[number], pose: string) => {
      const { svg, unmount } = mount(name);
      const limbs = [...(svg.querySelector(`[data-figure='${pose}']`)?.querySelectorAll("path") ?? [])].map((path) =>
        path.getAttribute("d"),
      );
      unmount();
      return limbs;
    };
    expect(limbsOf("lost", "reading")).not.toEqual(limbsOf("welcome", "waving"));
    expect(limbsOf("welcome", "waving")).not.toEqual(limbsOf("success", "holding"));
  });
});
