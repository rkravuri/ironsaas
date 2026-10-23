import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, onResponse, onUnauthenticated } from "@/api/client";
import { fetchContext } from "@/api/entitlements";
import {
  actFor as actForCall,
  endImpersonation as endImpersonationCall,
  fetchMe,
  logout as logoutCall,
  stopActing as stopActingCall,
  workFor as workForCall,
  type Me,
  type SsoProviderButton,
  type WorkFor,
} from "@/api/identity";
import type { KillSwitch } from "@/api/flags";
import { contextQueryKey, contextRefreshMs, entitlementsFrom } from "./entitlements";
import { flagProvider } from "./flags";
import { settingsFrom } from "./settings";
import { openSessionChannel, sessionEndOf, type SessionChannel, type SessionEnd } from "./session-channel";
import type { EndReason } from "./session-clock";
import { meQueryKey, SessionContext, type Session, type SessionStatus } from "./session-context";
import { SessionWatch, SessionWatchContext } from "./session-watch";

/** What another tab's end means here: its own sign-out is, for this tab, a sign-out elsewhere. */
const heardEnd = (reason: SessionEnd): SessionEnd => (reason === "signed-out" ? "signed-out-elsewhere" : reason);

/**
 * The signed-in principal from /me, shared by the shell, the guards and the pages (ADR-0020), and the session's times
 * for the warning (ADR-0033): /me sets them, every answer moves them, and the tabs of the browser share them. With the
 * session, /me/context: what the tenant is entitled to, read again every 30 seconds while the tab is visible, so a plan
 * changed in the control plane shows within a minute without a new sign-in (TF-043, ADR-0034), and the flags evaluated
 * for this person, which feed the shell's OpenFeature provider (TF-054, ADR-0035).
 */
export function SessionProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [watch] = useState(() => new SessionWatch());
  const channel = useRef<SessionChannel | undefined>(undefined);
  const [ended, setEnded] = useState<SessionEnd | undefined>();
  const [signedOutOf, setSignedOutOf] = useState<SsoProviderButton | undefined>();
  const [actingEnded, setActingEnded] = useState<string | undefined>();
  const [impersonationEnded, setImpersonationEnded] = useState(false);
  const query = useQuery({
    queryKey: meQueryKey,
    queryFn: ({ signal }) => fetchMe(signal),
    retry: false,
    staleTime: 60_000,
    refetchOnWindowFocus: true,
  });

  /**
   * Forgets the session here; tells the other tabs when this tab is where it ended. Only a live session can end. Every
   * cached answer goes with it, so the back button finds nothing of the session to show (TF-029 AC3).
   */
  const end = useCallback(
    (reason: SessionEnd | undefined, tell: boolean) => {
      const live = queryClient.getQueryData<Me | null>(meQueryKey);
      queryClient.setQueryData(meQueryKey, null);
      if (live) {
        queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== meQueryKey[0] });
      }
      queryClient.removeQueries({ queryKey: contextQueryKey });
      watch.clear();
      if (live && reason !== undefined) {
        setEnded(reason);
        if (tell) {
          channel.current?.post({ kind: "ended", reason });
        }
      }
    },
    [queryClient, watch],
  );

  const forget = useCallback(() => end(undefined, false), [end]);

  const refresh = useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: meQueryKey });
  }, [queryClient]);

  // The channel lives with the provider; opened in an effect so a second mount in development gets a channel of its own.
  useEffect(() => {
    const opened = openSessionChannel();
    channel.current = opened;
    const detach = watch.attach(opened);
    const stop = opened.listen((message) => {
      switch (message.kind) {
        case "times":
          watch.heard(message.times);
          break;
        case "ended":
          end(heardEnd(message.reason), false);
          break;
        case "signed-in":
          void refresh();
          break;
      }
    });
    return () => {
      stop();
      detach();
      opened.close();
      if (channel.current === opened) {
        channel.current = undefined;
      }
    };
  }, [watch, end, refresh]);

  // A 401 from any call: a session that was here has ended on the server (TF-021 AC1), and the login page says why: its
  // time, a newer sign-in past the tenant's limit, an administrator, a revocation, or the end of an impersonation.
  useEffect(() => onUnauthenticated((error) => end(sessionEndOf(error.errorCode), true)), [end]);
  useEffect(() => onResponse((info) => watch.response(info)), [watch]);

  /**
   * After acting for someone or as oneself again, or choosing another unit to work for: /me as the API answered it, and
   * every other answer read again, since what the person sees has changed with whose rights they use (ADR-0037) or with
   * the unit the lists keep to (ADR-0038).
   */
  const actingChanged = useCallback(
    async (next: Me) => {
      queryClient.setQueryData(meQueryKey, next);
      await queryClient.invalidateQueries({ predicate: (query) => query.queryKey[0] !== meQueryKey[0] });
    },
    [queryClient],
  );

  // The first answer after the delegation behind the acting ended says so: this tab says it too, by the name it knew, and
  // reads /me and the rest again as the person themselves.
  useEffect(
    () =>
      onResponse((info) => {
        if (info.actingEnded === undefined) {
          return;
        }
        const known = queryClient.getQueryData<Me | null>(meQueryKey)?.acting_for;
        setActingEnded(known?.id === info.actingEnded ? known.name : "");
        void queryClient.invalidateQueries();
      }),
    [queryClient],
  );

  // The first answer after an impersonation ended ran as the impersonator again (TF-025 AC3): this tab says so, and reads
  // /me and the rest again as them.
  useEffect(
    () =>
      onResponse((info) => {
        if (info.impersonationEnded) {
          setImpersonationEnded(true);
          void queryClient.invalidateQueries();
        }
      }),
    [queryClient],
  );

  const endImpersonation = useCallback(async () => {
    const operator = queryClient.getQueryData<Me | null>(meQueryKey)?.impersonation?.operator === true;
    await endImpersonationCall();
    if (operator) {
      // The operator has no session of their own here: the impersonation was all there was.
      end("impersonation-ended", true);
      return;
    }
    setImpersonationEnded(true);
    await queryClient.invalidateQueries();
  }, [end, queryClient]);

  const dismissImpersonationEnded = useCallback(() => setImpersonationEnded(false), []);

  const actFor = useCallback(
    async (delegatorId: string) => {
      setActingEnded(undefined);
      await actingChanged(await actForCall(delegatorId));
    },
    [actingChanged],
  );

  const stopActing = useCallback(async () => {
    await actingChanged(await stopActingCall());
  }, [actingChanged]);

  const workFor = useCallback(
    async (choice: WorkFor) => {
      await actingChanged(await workForCall(choice));
    },
    [actingChanged],
  );

  const dismissActingEnded = useCallback(() => setActingEnded(undefined), []);

  // Each /me answer sets the times, on this browser's clock, from the moment it arrived.
  const me = query.data ?? undefined;

  const context = useQuery({
    queryKey: contextQueryKey,
    queryFn: ({ signal }) => fetchContext(signal),
    enabled: me !== undefined,
    retry: false,
    staleTime: contextRefreshMs,
    // Every 30 seconds while the tab is visible, never while it is hidden, and at once when it is shown again, however
    // fresh the last answer: a person coming back to the tab sees what applies now, and a hidden tab costs nothing.
    refetchInterval: contextRefreshMs,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: "always",
  });
  const contextData = me !== undefined ? context.data : undefined;
  const entitlements = useMemo(
    () => entitlementsFrom(contextData, me !== undefined && context.isPending),
    [contextData, me, context.isPending],
  );
  const flags = contextData?.flags;
  const killSwitches = useMemo<readonly KillSwitch[]>(() => flags?.kill_switches ?? [], [flags]);
  const contextSettings = contextData?.settings;
  const settings = useMemo(() => settingsFrom(contextSettings), [contextSettings]);
  const settingsKnown = contextSettings !== undefined;
  const release = contextData?.release;
  // Without an answer (signed out, still loading, or the call failed) the provider knows nothing and every flag has its
  // default. A layout effect, so the page re-renders with the new values before it paints: no flash of a hidden control.
  useLayoutEffect(() => flagProvider.update(flags?.values ?? []), [flags]);
  useEffect(() => {
    if (me) {
      watch.fromMe(me.session, query.dataUpdatedAt);
    }
  }, [me, query.dataUpdatedAt, watch]);

  const signedIn = useCallback(async () => {
    setEnded(undefined);
    setSignedOutOf(undefined);
    await refresh();
    channel.current?.post({ kind: "signed-in" });
  }, [refresh]);

  const extend = useCallback(async () => {
    queryClient.setQueryData(meQueryKey, await fetchMe());
  }, [queryClient]);

  const signOut = useCallback(async () => {
    const provider = queryClient.getQueryData<Me | null>(meQueryKey)?.signed_in_with;
    try {
      await logoutCall();
    } finally {
      setSignedOutOf(provider ? { id: provider.provider_id, name: provider.name } : undefined);
      end("signed-out", true);
    }
  }, [end, queryClient]);

  const closed = useCallback((reason: SessionEnd) => end(reason, true), [end]);

  const expire = useCallback(
    (reason: EndReason) => {
      // Forgotten first, so every tab leaves at once; the server's session ends behind it, or has already.
      end(reason, true);
      void logoutCall().catch(() => undefined);
    },
    [end],
  );

  const value = useMemo<Session>(() => {
    let status: SessionStatus;
    if (query.isPending) {
      status = "loading";
    } else if (me) {
      status = "signed-in";
    } else {
      status = "signed-out";
    }
    if (query.error && !(query.error instanceof ApiError && query.error.isUnauthenticated)) {
      // A failure that is not "not signed in" still shows the login page, with the error noted by the boundary later.
      status = query.isPending ? "loading" : "signed-out";
    }
    return {
      status,
      me,
      permissions: me?.permissions_anywhere ?? [],
      tenantPermissions: me?.permissions ?? [],
      entitlements,
      killSwitches,
      settings,
      settingsKnown,
      release,
      ended,
      signedOutOf,
      actingEnded,
      impersonationEnded,
      refresh,
      signedIn,
      extend,
      signOut,
      expire,
      closed,
      forget,
      actFor,
      stopActing,
      workFor,
      dismissActingEnded,
      endImpersonation,
      dismissImpersonationEnded,
    };
  }, [
    me,
    query.error,
    query.isPending,
    entitlements,
    killSwitches,
    settings,
    settingsKnown,
    release,
    ended,
    signedOutOf,
    actingEnded,
    impersonationEnded,
    refresh,
    signedIn,
    extend,
    signOut,
    expire,
    closed,
    forget,
    actFor,
    stopActing,
    workFor,
    dismissActingEnded,
    endImpersonation,
    dismissImpersonationEnded,
  ]);

  return (
    <SessionWatchContext.Provider value={watch}>
      <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
    </SessionWatchContext.Provider>
  );
}
