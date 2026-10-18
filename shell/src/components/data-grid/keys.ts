export type RowKeyAction = "select" | "open";

type Keys = Pick<KeyboardEvent, "key" | "ctrlKey" | "metaKey" | "altKey" | "shiftKey">;

/**
 * The keyboard on a focused row (TF-172 AC2): Enter selects it (Space does too, through the grid itself), and
 * Ctrl+Enter, or Cmd+Enter on a Mac, runs the row's action. Anything with Alt or Shift is left to the grid.
 */
export function rowKeyAction(event: Keys): RowKeyAction | undefined {
  if (event.key !== "Enter" || event.altKey || event.shiftKey) {
    return undefined;
  }
  return event.ctrlKey || event.metaKey ? "open" : "select";
}
