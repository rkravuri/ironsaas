import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, onResponse, onUnauthenticated } from "@/api/client";
import { fetchMe, logout as logoutCall, type Me } from "@/api/identity";
import { openSessionChannel, type SessionChannel, type SessionEnd } from "./session-channel";
import type { EndReason } from "./session-clock";
import { meQueryKey, SessionContext, type Session, type SessionStatus } from "./session-context";
import { SessionWatch, SessionWatchContext } from "./session-watch";

/** What another tab's end means here: its own sign-out is, for this tab, a sign-out elsewhere. */
const heardEnd = (reason: SessionEnd): SessionEnd => (reason === "signed-out" ? "signed-out-elsewhere" : reason);

/**
 * The signed-in principal from /me, shared by the shell, the guards and the pages (ADR-0020), and the session's times
 * for the warning (ADR-0033): /me sets them, every answer moves them, and the tabs of the browser share them.
 */
export function SessionProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [watch] = useState(() => new SessionWatch());
  const channel = useRef<SessionChannel | undefined>(undefined);
  const [ended, setEnded] = useState<SessionEnd | undefined>();
  const query = useQuery({
    queryKey: meQueryKey,
    queryFn: ({ signal }) => fetchMe(signal),
    retry: false,
    staleTime: 60_000,
    refetchOnWindowFocus: true,
  });

  /** Forgets the session here; tells the other tabs when this tab is where it ended. Only a live session can end. */
  const end = useCallback(
    (reason: SessionEnd | undefined, tell: boolean) => {
      const live = queryClient.getQueryData<Me | null>(meQueryKey);
      queryClient.setQueryData(meQueryKey, null);
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

  // A 401 from any call: a session that was here has ended on the server (TF-021 AC1), and the login page says so.
  useEffect(() => onUnauthenticated(() => end("expired", true)), [end]);
  useEffect(() => onResponse((info) => watch.response(info)), [watch]);

  // Each /me answer sets the times, on this browser's clock, from the moment it arrived.
  const me = query.data ?? undefined;
  useEffect(() => {
    if (me) {
      watch.fromMe(me.session, query.dataUpdatedAt);
    }
  }, [me, query.dataUpdatedAt, watch]);

  const signedIn = useCallback(async () => {
    setEnded(undefined);
    await refresh();
    channel.current?.post({ kind: "signed-in" });
  }, [refresh]);

  const extend = useCallback(async () => {
    queryClient.setQueryData(meQueryKey, await fetchMe());
  }, [queryClient]);

  const signOut = useCallback(async () => {
    try {
      await logoutCall();
    } finally {
      end("signed-out", true);
    }
  }, [end]);

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
    return { status, me, permissions: me?.permissions_anywhere ?? [], tenantPermissions: me?.permissions ?? [], ended, refresh, signedIn, extend, signOut, expire, forget };
  }, [me, query.error, query.isPending, ended, refresh, signedIn, extend, signOut, expire, forget]);

  return (
    <SessionWatchContext.Provider value={watch}>
      <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
    </SessionWatchContext.Provider>
  );
}
