import { useMemo } from "react";
import { createBrowserRouter, Navigate, RouterProvider, type RouteObject } from "react-router";
import { useSession } from "@/auth/session-context";
import { LoginPage } from "@/auth/LoginPage";
import { InvitationPage } from "@/auth/InvitationPage";
import { manifests } from "@/modules";
import { navigationFor, routesFor, validateManifests } from "@/modules/registry";
import { Layout } from "@/shell/Layout";
import { RouteErrorPage } from "@/shell/ErrorBoundary";
import { RedirectIfSignedIn, RequirePermission, RequireSession } from "@/shell/Guards";

const strict = import.meta.env.DEV;

// Checked once at start: a manifest mistake fails the development build loudly and is hidden in production.
validateManifests(manifests, { strict });

/** The router: public pages, then the shell with the routes the signed-in principal may see. */
export function App() {
  const session = useSession();
  const router = useMemo(() => {
    const permissions = session.permissions;
    const navigation = navigationFor(manifests, permissions, { strict });
    const routes = routesFor(manifests, permissions, { strict });
    const home = navigation[0]?.entries[0]?.path ?? "/me";
    const children: RouteObject[] = routes.map((route) => ({
      path: route.path.replace(/^\//, ""),
      element: <RequirePermission permission={route.permission ?? null}>{route.element}</RequirePermission>,
    }));
    return createBrowserRouter([
      { path: "/login", element: <RedirectIfSignedIn><LoginPage /></RedirectIfSignedIn>, errorElement: <RouteErrorPage /> },
      { path: "/invitations/:token", element: <InvitationPage />, errorElement: <RouteErrorPage /> },
      {
        path: "/",
        element: (
          <RequireSession>
            <Layout navigation={navigation} />
          </RequireSession>
        ),
        errorElement: <RouteErrorPage />,
        children: [{ index: true, element: <Navigate to={home} replace /> }, ...children],
      },
    ]);
  }, [session.permissions]);

  return <RouterProvider router={router} />;
}
