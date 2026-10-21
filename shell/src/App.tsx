import { lazy, Suspense } from "react";
import { createBrowserRouter, RouterProvider, type RouteObject } from "react-router";
import { manifests } from "@/modules";
import { validateManifests } from "@/modules/registry";
import { RouteErrorPage } from "@/shell/ErrorBoundary";
import { LoadingView } from "@/shell/Guards";
import { shellRoutes } from "@/shell/routes";

const strict = import.meta.env.DEV;

// Checked once at start: a manifest mistake fails the development build loudly and is hidden in production.
validateManifests(manifests, { strict });

// The gallery lives in development builds only (ADR-0025): the production bundle has neither the route nor the chunk.
const GalleryPage = import.meta.env.DEV ? lazy(() => import("@/gallery/GalleryPage")) : null;

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

/** The router, built once with every declared route (src/shell/routes.tsx). */
const router = createBrowserRouter(shellRoutes(manifests, { strict, extra: galleryRoutes }));

export function App() {
  return <RouterProvider router={router} />;
}
