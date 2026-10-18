import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Navigate, useLocation } from "react-router";
import { Center, Loader, Stack, Text } from "@mantine/core";
import { useSession } from "@/auth/session-context";
import { ErrorView } from "./ErrorBoundary";
import { ApiError } from "@/api/client";

/** Signed-in principals only; others go to the login page and come back afterwards. */
export function RequireSession({ children }: { children: ReactNode }) {
  const session = useSession();
  const location = useLocation();
  const { t } = useTranslation();
  if (session.status === "loading") {
    return (
      <Center h="100vh">
        <Stack align="center" gap="xs">
          <Loader />
          <Text c="dimmed" size="sm">
            {t("app.loading")}
          </Text>
        </Stack>
      </Center>
    );
  }
  if (session.status === "signed-out") {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
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

/** Signed-in principals do not see the login page; they go home. */
export function RedirectIfSignedIn({ children }: { children: ReactNode }) {
  const session = useSession();
  if (session.status === "signed-in") {
    return <Navigate to="/" replace />;
  }
  return <>{children}</>;
}
