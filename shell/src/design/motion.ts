import { useMediaQuery } from "@mantine/hooks";

export const reducedMotionQuery = "(prefers-reduced-motion: reduce)";

/** True when the viewer asked the operating system for less motion; every transition is then a cut (global.css). */
export function usePrefersReducedMotion(): boolean {
  return useMediaQuery(reducedMotionQuery, false, { getInitialValueInEffect: false });
}
