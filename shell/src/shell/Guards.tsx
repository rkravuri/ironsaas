import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router";
import { useSession } from "@/auth/session-context";
import { useSignInHeld } from "@/auth/sign-in-hold";
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
    const state: LoginState = {
      from: location.pathname + location.search,
      ...(session.ended !== undefined ? { reason: session.ended } : {}),
    };
    return <Navigate to="/login" replace state={state} />;
  }
  return <>{children}</>;
}

/**
 * The route's licence and permission, checked again at render time: a link typed by hand gets the same answer as the
 * API would give (TF-030, TF-040 AC1). A licensed module's page waits for the first answer of /me/context.
 */
export function RequirePermission({
  permission,
  licence,
  children,
}: {
  permission: string | null;
  licence?: string | undefined;
  children: ReactNode;
}) {
  const session = useSession();
  if (licence !== undefined && !session.entitlements.entitled(licence)) {
    return session.entitlements.pending ? (
      <LoadingView height="60vh" />
    ) : (
      <ErrorView
        error={new ApiError(403, { title: "Forbidden", error_code: "entitlements.module_disabled" }, undefined)}
      />
    );
  }
  if (permission !== null && !session.permissions.includes(permission)) {
    return <ErrorView error={new ApiError(403, { title: "Forbidden", error_code: "authz.forbidden" }, undefined)} />;
  }
  return <>{children}</>;
}

/** Signed-in principals do not see the login page: they go back where they were, or home. */
export function RedirectIfSignedIn({ children }: { children: ReactNode }) {
  const session = useSession();
  const location = useLocation();
  // Not while the login page shows recovery codes: they are shown once, and the person goes on when they saved them.
  const held = useSignInHeld();
  if (session.status === "signed-in" && !held) {
    return <Navigate to={(location.state as LoginState | null)?.from ?? "/"} replace />;
  }
  return <>{children}</>;
}
