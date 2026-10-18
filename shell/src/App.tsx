import { lazy, Suspense } from "react";
import { createBrowserRouter, Navigate, RouterProvider, type RouteObject } from "react-router";
import { useSession } from "@/auth/session-context";
import { LoginPage } from "@/auth/LoginPage";
import { InvitationPage } from "@/auth/InvitationPage";
import { manifests } from "@/modules";
import { declaredRoutes, navigationFor, validateManifests } from "@/modules/registry";
import { Layout } from "@/shell/Layout";
import { RouteErrorPage } from "@/shell/ErrorBoundary";
import { LoadingView, RedirectIfSignedIn, RequirePermission, RequireSession } from "@/shell/Guards";

const strict = import.meta.env.DEV;

// Checked once at start: a manifest mistake fails the development build loudly and is hidden in production.
validateManifests(manifests, { strict });

// The gallery lives in development builds only (ADR-0025): the production bundle has neither the route nor the chunk.
const GalleryPage = import.meta.env.DEV ? lazy(() => import("@/gallery/GalleryPage")) : null;

/** The first page the signed-in principal may see, by the navigation its permissions leave visible. */
function Home() {
  const session = useSession();
  const navigation = navigationFor(manifests, session.permissions, { strict });
  return <Navigate to={navigation[0]?.entries[0]?.path ?? "/me"} replace />;
}

const children: RouteObject[] = declaredRoutes(manifests, { strict }).map((route) => ({
  path: route.path.replace(/^\//, ""),
  element: <RequirePermission permission={route.permission ?? null}>{route.element}</RequirePermission>,
}));

const galleryRoutes: RouteObject[] = GalleryPage
  ? [
      {
        path: "/gallery",
        element: (
          <Suspense fallback={<LoadingView />}>
            <GalleryPage />
          </Suspense>
        ),
        errorElement: <RouteErrorPage />,
      },
    ]
  : [];

/**
 * The router, built once with every declared route: public pages, then the shell with the pages the signed-in
 * principal may open. Permissions decide at render time (and the API decides for real), so a deep link or a reload
 * lands on the page, on the login page, or on "no access", never on "not found".
 */
const router = createBrowserRouter([
  {
    path: "/login",
    element: (
      <RedirectIfSignedIn>
        <LoginPage />
      </RedirectIfSignedIn>
    ),
    errorElement: <RouteErrorPage />,
  },
  { path: "/invitations/:token", element: <InvitationPage />, errorElement: <RouteErrorPage /> },
  ...galleryRoutes,
  {
    path: "/",
    element: (
      <RequireSession>
        <Layout />
      </RequireSession>
    ),
    errorElement: <RouteErrorPage />,
    children: [{ index: true, element: <Home /> }, ...children],
  },
]);

export function App() {
  return <RouterProvider router={router} />;
}
