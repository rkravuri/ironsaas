import { createContext, useContext } from "react";
import type { Me } from "@/api/identity";

export type SessionStatus = "loading" | "signed-in" | "signed-out";

export interface Session {
  status: SessionStatus;
  me: Me | undefined;
  /** Every permission held at any scope: navigation and route guards follow it; the API still decides per row (ADR-0031). */
  permissions: readonly string[];
  /** The permissions held for the whole tenant: the actions whose scope is the tenant, such as changing roles. */
  tenantPermissions: readonly string[];
  /** Re-reads /me, after a login. */
  refresh: () => Promise<void>;
  /** Ends the session on the server and forgets it here. */
  signOut: () => Promise<void>;
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
