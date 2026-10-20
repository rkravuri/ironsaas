import { useMemo } from "react";
import { useAccessAt } from "@/auth/access";
import { useSession } from "@/auth/session-context";
import { manifests } from "@/modules";
import { quickActionsFor, type QuickAction } from "@/modules/registry";

export interface QuickActions {
  /** True while /me/access is on its way; the actions stay hidden until it answers. */
  pending: boolean;
  actions: (QuickAction & { module: string })[];
}

/**
 * The quick actions the principal may perform (ADR-0033): each one's permission held at the principal's home unit, as
 * /me/access answers there, or for the whole tenant when the principal has no home unit. The page that owns the action
 * checks again, and the API decides.
 */
export function useQuickActions(): QuickActions {
  const session = useSession();
  const candidates = useMemo(
    () => quickActionsFor(manifests, session.permissions, session.entitlements.entitled),
    [session.permissions, session.entitlements],
  );
  const home = session.me?.home_unit?.id;
  const access = useAccessAt(
    home !== undefined ? { unit: home } : undefined,
    candidates.map((action) => action.permission),
  );
  const actions = candidates.filter((action) =>
    home !== undefined ? access.can(action.permission) : session.tenantPermissions.includes(action.permission),
  );
  return { pending: access.pending, actions };
}
