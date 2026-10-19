/** A key or a chord, one entry per key: ["mod", "K"]. "mod" is Ctrl, or Command on a Mac. */
export interface Shortcut {
  keys: string[];
  /** i18n key of what the keys do. */
  labelKey: string;
}

/** True on a Mac, where "mod" is the Command key. */
export const onMac = (): boolean => typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.userAgent);

/** One key as a person reads it on their keyboard; the catalogue names the keys that are words or arrows. */
export function keyName(key: string, mac: boolean, t: (key: string) => string): string {
  if (key === "mod") {
    return mac ? "⌘" : "Ctrl";
  }
  return t(`shortcuts.keys.${key}`) || key;
}

/** A chord holds a modifier; otherwise the keys are alternatives (up or down). */
export const isChord = (shortcut: Shortcut): boolean => shortcut.keys.some((key) => key === "mod" || key === "shift");

/** The keys every page shares: the search, closing what is open, and moving between controls. */
export const shellShortcuts: Shortcut[] = [
  { keys: ["mod", "K"], labelKey: "shortcuts.shell.search" },
  { keys: ["escape"], labelKey: "shortcuts.shell.close" },
  { keys: ["tab"], labelKey: "shortcuts.shell.next" },
  { keys: ["shift", "tab"], labelKey: "shortcuts.shell.previous" },
];

/** The data grid's keys (ADR-0027 item 5), for any page whose work is a grid. */
export const gridShortcuts: Shortcut[] = [
  { keys: ["up", "down"], labelKey: "shortcuts.grid.move" },
  { keys: ["space"], labelKey: "shortcuts.grid.select" },
  { keys: ["mod", "enter"], labelKey: "shortcuts.grid.open" },
];

/** The tree's keys, for the organisation tree and the unit picker. */
export const treeShortcuts: Shortcut[] = [
  { keys: ["up", "down"], labelKey: "shortcuts.tree.move" },
  { keys: ["right", "left"], labelKey: "shortcuts.tree.expand" },
];

/** The keys of a record's tabs. */
export const tabShortcuts: Shortcut[] = [{ keys: ["left", "right"], labelKey: "shortcuts.tabs.move" }];
