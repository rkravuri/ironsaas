import { useCallback, useEffect, useMemo, type ReactNode } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, onUnauthenticated } from "@/api/client";
import { fetchMe, logout as logoutCall } from "@/api/identity";
import { meQueryKey, SessionContext, type Session, type SessionStatus } from "./session-context";

/** The signed-in principal from /me, shared by the shell, the guards and the pages (ADR-0020). */
export function SessionProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: meQueryKey,
    queryFn: ({ signal }) => fetchMe(signal),
    retry: false,
    staleTime: 60_000,
    refetchOnWindowFocus: true,
  });

  const forget = useCallback(() => {
    queryClient.setQueryData(meQueryKey, null);
  }, [queryClient]);

  useEffect(() => onUnauthenticated(forget), [forget]);

  const refresh = useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: meQueryKey });
  }, [queryClient]);

  const signOut = useCallback(async () => {
    try {
      await logoutCall();
    } finally {
      forget();
    }
  }, [forget]);

  const value = useMemo<Session>(() => {
    const me = query.data ?? undefined;
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
    return { status, me, permissions: me?.permissions ?? [], refresh, signOut, forget };
  }, [query.data, query.error, query.isPending, refresh, signOut, forget]);

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}
