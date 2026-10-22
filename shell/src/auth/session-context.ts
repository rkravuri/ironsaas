import { createContext, useContext } from "react";
import type { KillSwitch } from "@/api/flags";
import type { Me, SsoProviderButton, WorkFor } from "@/api/identity";
import type { Entitlements } from "./entitlements";
import type { EffectiveSettings } from "./settings";
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
  /** The kill switches that are on, from /me/context: the API sends them to administrators only (ADR-0035). */
  killSwitches: readonly KillSwitch[];
  /** The settings the shell may see, for this person, from /me/context (ADR-0036); read them with useSetting. */
  settings: EffectiveSettings;
  /** False until /me/context has answered with its settings: a preference is not synced before the server's is known. */
  settingsKnown: boolean;
  /** Why the last session ended, for the sentence on the login page; undefined when nothing ended it here. */
  ended: SessionEnd | undefined;
  /**
   * The company login the session this tab signed out of came through (ADR-0048): the login page offers to sign out
   * there too. Undefined after a password, a passkey, or any other end.
   */
  signedOutOf: SsoProviderButton | undefined;
  /**
   * The delegator's name when the delegation this session acted with has ended and the API said so (ADR-0037); an empty
   * string when this tab did not know the name; undefined when nothing ended.
   */
  actingEnded: string | undefined;
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
  /** Acts for someone whose delegation to this person is active: their rights for the delegated duties (ADR-0037). */
  actFor: (delegatorId: string) => Promise<void>;
  /** Acts as oneself again. */
  stopActing: () => Promise<void>;
  /** Works for one of the person's units, or all of them (ADR-0038): new records and settings follow it, lists keep to it. */
  workFor: (choice: WorkFor) => Promise<void>;
  /** The person read the notice that the acting ended. */
  dismissActingEnded: () => void;
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
