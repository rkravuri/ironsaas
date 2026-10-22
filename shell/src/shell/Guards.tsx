import { useEffect, type ReactNode } from "react";
import { Navigate, useLocation, useNavigate } from "react-router";
import { useSession } from "@/auth/session-context";
import { useSignInHeld } from "@/auth/sign-in-hold";
import { LoadingState } from "@/components/panels";
import { ErrorView } from "./ErrorBoundary";
import { ApiError, onEnrolmentRequired } from "@/api/client";
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
 * While the tenant's policy requires a second factor the person has not got (ADR-0047), the frame's pages wait: `/me`
 * says so, or any call answers 403 `identity.mfa.enrolment_required`, and the person goes to enrolment, signed in, then
 * back to the page they were on.
 */
export function RequireEnrolled({ children }: { children: ReactNode }) {
  const session = useSession();
  const location = useLocation();
  const navigate = useNavigate();
  const here = location.pathname + location.search;
  useEffect(
    () =>
      onEnrolmentRequired(
        () => void navigate("/enrol", { replace: true, state: { from: here } satisfies EnrolmentState }),
      ),
    [navigate, here],
  );
  if (session.me?.mfa_enrolment_required) {
    return <Navigate to="/enrol" replace state={{ from: here } satisfies EnrolmentState} />;
  }
  return <>{children}</>;
}

/** Where the enrolment page returns the person to. */
export interface EnrolmentState {
  from?: string;
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
