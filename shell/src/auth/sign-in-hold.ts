import { useSyncExternalStore } from "react";

/**
 * While the login page shows recovery codes, the session exists already, and a /me read on window focus would take the
 * person away from the codes, which are shown only once (ADR-0047). The page holds the redirect until the person says
 * they saved them.
 */
let held = false;
const listeners = new Set<() => void>();

export function holdSignIn(hold: boolean): void {
  if (held !== hold) {
    held = hold;
    for (const listener of listeners) {
      listener();
    }
  }
}

export function useSignInHeld(): boolean {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => held,
    () => false,
  );
}
