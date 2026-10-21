import { describe, expect, it } from "vitest";
import { rowKeyAction } from "./keys";

const keys = (
  key: string,
  modifiers: Partial<Record<"ctrlKey" | "metaKey" | "altKey" | "shiftKey", boolean>> = {},
) => ({
  key,
  ctrlKey: false,
  metaKey: false,
  altKey: false,
  shiftKey: false,
  ...modifiers,
});

describe("the keyboard on a row", () => {
  it("selects on Enter and opens on Ctrl+Enter or Cmd+Enter [ADR-0027]", () => {
    expect(rowKeyAction(keys("Enter"))).toBe("select");
    expect(rowKeyAction(keys("Enter", { ctrlKey: true }))).toBe("open");
    expect(rowKeyAction(keys("Enter", { metaKey: true }))).toBe("open");
  });

  it("leaves every other key, and Enter with Alt or Shift, to the grid [ADR-0027]", () => {
    expect(rowKeyAction(keys(" "))).toBeUndefined();
    expect(rowKeyAction(keys("ArrowDown"))).toBeUndefined();
    expect(rowKeyAction(keys("Enter", { altKey: true }))).toBeUndefined();
    expect(rowKeyAction(keys("Enter", { shiftKey: true }))).toBeUndefined();
  });
});
