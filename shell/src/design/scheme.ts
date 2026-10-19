import { localStorageColorSchemeManager } from "@mantine/core";

/**
 * The viewer's colour scheme (ADR-0025 item 8, ADR-0033): Light, Dark or the device's, chosen in the profile menu and
 * kept in this browser as a per-viewer convenience. `public/scheme.js` reads the same key before the first paint, so a
 * reload never flashes the wrong scheme; the scheme test holds the two to one key.
 */
export const schemeStorageKey = "iron.scheme";

export const schemeChoices = ["light", "dark", "auto"] as const;
export type SchemeChoice = (typeof schemeChoices)[number];

/** Mantine's manager on our key; it also follows a choice made in another tab of this browser. */
export const colorSchemeManager = localStorageColorSchemeManager({ key: schemeStorageKey });
