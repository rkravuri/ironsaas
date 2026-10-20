import { createContext, useContext } from "react";
import type { Me } from "@/api/identity";
import type { Entitlements } from "./entitlements";
import type { SessionEnd } from "./session-channel";
import type { EndReason } from "./session-clock";

export type SessionStatus = "loading" | "signed-in" | "signed-out";

export interface Session {
  status: SessionStatus;
  me: Me | undefined;
  /** Every permission held at any scope: navigation and route guards follow it; the API still decides per row (ADR-0031). */
  permissions: readonly string[];
  /** The permissions held for the whole tenant: the actions whose scope is the tenant, such as changing roles. */
  tenantPermissions: readonly string[];
  /** What this tenant, and this person's seats, entitle (ADR-0034): from /me/context, read with the session and every 30 seconds. */
  entitlements: Entitlements;
  /** Why the last session ended, for the sentence on the login page; undefined when nothing ended it here. */
  ended: SessionEnd | undefined;
  /** Re-reads /me. */
  refresh: () => Promise<void>;
  /** After a login: re-reads /me and tells the other tabs, so one still on the login page follows. */
  signedIn: () => Promise<void>;
  /** "Stay signed in": one request that touches the session, GET /me; its answer moves the idle end in every tab. */
  extend: () => Promise<void>;
  /** Ends the session on the server and here, in every tab. */
  signOut: () => Promise<void>;
  /** The session's clock ran out: forget it in every tab at once, then end it on the server. */
  expire: (reason: EndReason) => void;
  /** Forgets the session here, after a 401 from any call. */
  forget: () => void;
}

export const SessionContext = createContext<Session | undefined>(undefined);

export const meQueryKey = ["me"] as const;

export function useSession(): Session {
  const session = useContext(SessionContext);
  if (!session) {
    throw new Error("useSession needs a SessionProvider above it.");
  }
  return session;
}
