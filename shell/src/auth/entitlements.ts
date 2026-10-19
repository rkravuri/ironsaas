import type { Context, EntitlementStatus } from "@/api/entitlements";
import { useSession } from "./session-context";

/** The query of /me/context: under "me", so whatever refreshes the session refreshes it too. */
export const contextQueryKey = ["me", "context"] as const;

/**
 * How often the shell reads /me/context (ADR-0034): every 30 seconds, as the unit reads the store, so a change in the
 * control plane reaches the shell within a minute (TF-043).
 */
export const contextRefreshMs = 30_000;

/** What a module or feature allows now: seen at all, and changed. */
export interface Access {
  available: boolean;
  writable: boolean;
  status: EntitlementStatus;
  reason: string;
}

export interface Entitlements {
  /** True until the first answer of /me/context; licensed modules and features stay hidden until it comes. */
  pending: boolean;
  /** The unit could not confirm the entitlements for 24 hours: everything licensed is disabled. */
  stale: boolean;
  /** A licensed module the person may open: enabled or read only, with a seat if it needs one. */
  entitled: (licence: string) => boolean;
  module: (licence: string) => Access;
  feature: (code: string) => Access;
}

const unknown: Access = { available: false, writable: false, status: "disabled", reason: "unknown" };

const access = (status: EntitlementStatus, reason: string): Access => ({ available: status !== "disabled", writable: status === "enabled", status, reason });

/** The entitlements from an answer of /me/context; without one, nothing licensed is available. */
export function entitlementsFrom(context: Context | undefined, pending: boolean): Entitlements {
  const modules = new Map((context?.entitlements.modules ?? []).map((entry) => [entry.code, access(entry.status, entry.reason)]));
  const features = new Map((context?.entitlements.features ?? []).map((entry) => [entry.code, access(entry.status, entry.reason)]));
  return {
    pending,
    stale: context?.entitlements.stale ?? false,
    entitled: (licence) => modules.get(licence)?.available ?? false,
    module: (licence) => modules.get(licence) ?? unknown,
    feature: (code) => features.get(code) ?? unknown,
  };
}

/**
 * A feature for the signed-in person (TF-046 AC2): show its controls when it is available, its writes when it is
 * writable. The API refuses it regardless (TF-043 AC2), so this only decides what the shell offers.
 */
export function useFeature(code: string): Access {
  return useSession().entitlements.feature(code);
}
