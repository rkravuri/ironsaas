import type { RouteObject } from "react-router";
import { EnrolmentPage } from "@/auth/EnrolmentPage";
import { InvitationPage } from "@/auth/InvitationPage";
import { LoginPage } from "@/auth/LoginPage";
import { RecoveryPage } from "@/auth/RecoveryPage";
import { HomePage } from "@/home/HomePage";
import { declaredRoutes, type ModuleManifest } from "@/modules/registry";
import { RouteErrorPage } from "./ErrorBoundary";
import { RedirectIfSignedIn, RequireEnrolled, RequirePermission, RequireSession } from "./Guards";
import { Layout } from "./Layout";

/**
 * The shell's routes, built once from every declared route (ADR-0020): the public pages, any extra ones (the gallery in
 * development), then the shell with the pages the signed-in principal may open. Permissions decide at render time (and
 * the API decides for real), so a deep link or a reload lands on the page, on the login page, or on "no access".
 *
 * Every page has an error element of its own: a page that fails shows the friendly error with its correlation id inside
 * the shell, whose header and navigation stay usable, and moving to another page leaves the error behind (TF-170 AC2).
 * The shell's own error element catches what fails around the pages.
 */
export function shellRoutes(
  manifests: ModuleManifest[],
  { strict, extra = [] }: { strict: boolean; extra?: RouteObject[] },
): RouteObject[] {
  const pages: RouteObject[] = declaredRoutes(manifests, { strict }).map((route) => ({
    path: route.path.replace(/^\//, ""),
    element: (
      <RequirePermission permission={route.permission ?? null} licence={route.licence}>
        {route.element}
      </RequirePermission>
    ),
    errorElement: <RouteErrorPage />,
  }));
  return [
    {
      path: "/login",
      element: (
        <RedirectIfSignedIn>
          <LoginPage />
        </RedirectIfSignedIn>
      ),
      errorElement: <RouteErrorPage />,
    },
    // The links carry their code in the fragment (ADR-0046); /invitations/:token keeps the links issued before working.
    { path: "/invitations", element: <InvitationPage />, errorElement: <RouteErrorPage /> },
    { path: "/invitations/:token", element: <InvitationPage />, errorElement: <RouteErrorPage /> },
    { path: "/recovery", element: <RecoveryPage />, errorElement: <RouteErrorPage /> },
    // Signed in, outside the frame: the second factor the tenant's policy now requires, before any page (ADR-0047).
    {
      path: "/enrol",
      element: (
        <RequireSession>
          <EnrolmentPage />
        </RequireSession>
      ),
      errorElement: <RouteErrorPage />,
    },
    ...extra,
    {
      path: "/",
      element: (
        <RequireSession>
          <RequireEnrolled>
            <Layout />
          </RequireEnrolled>
        </RequireSession>
      ),
      errorElement: <RouteErrorPage />,
      // Home is the landing page after sign-in (ADR-0033): any signed-in principal may open it.
      children: [{ index: true, element: <HomePage />, errorElement: <RouteErrorPage /> }, ...pages],
    },
  ];
}
