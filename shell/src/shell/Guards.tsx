import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router";
import { useSession } from "@/auth/session-context";
import { LoadingState } from "@/components/panels";
import { ErrorView } from "./ErrorBoundary";
import { ApiError } from "@/api/client";
import type { SessionEnd } from "@/auth/session-channel";

/** The wait before the session is known: the loading state, full height. */
export function LoadingView({ height = "100vh" }: { height?: string | number } = {}) {
  return <LoadingState height={height} />;
}

/** Where the login page returns to, and why the person is there (TF-021): the router state the guards pass along. */
export interface LoginState {
  from?: string;
  reason?: SessionEnd;
}

/** Signed-in principals only; others go to the login page, told why the session ended, and come back afterwards. */
export function RequireSession({ children }: { children: ReactNode }) {
  const session = useSession();
  const location = useLocation();
  if (session.status === "loading") {
    return <LoadingView />;
  }
  if (session.status === "signed-out") {
    const state: LoginState = { from: location.pathname + location.search, ...(session.ended !== undefined ? { reason: session.ended } : {}) };
    return <Navigate to="/login" replace state={state} />;
  }
  return <>{children}</>;
}

/** The route's permission, checked again at render time: a link typed by hand gets the same answer as the API would give (TF-030). */
export function RequirePermission({ permission, children }: { permission: string | null; children: ReactNode }) {
  const session = useSession();
  if (permission !== null && !session.permissions.includes(permission)) {
    return <ErrorView error={new ApiError(403, { title: "Forbidden", error_code: "authz.forbidden" }, undefined)} />;
  }
  return <>{children}</>;
}

/** Signed-in principals do not see the login page: they go back where they were, or home. */
export function RedirectIfSignedIn({ children }: { children: ReactNode }) {
  const session = useSession();
  const location = useLocation();
  if (session.status === "signed-in") {
    return <Navigate to={(location.state as LoginState | null)?.from ?? "/"} replace />;
  }
  return <>{children}</>;
}
