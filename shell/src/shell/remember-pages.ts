import { useEffect } from "react";
import { useLocation } from "react-router";
import { useSession } from "@/auth/session-context";
import { rememberPage } from "@/home/recent";
import { manifests } from "@/modules";
import { routeAt } from "@/modules/registry";

/** Remembers every page the person opens in the shell for the home page, except home itself and the query string. */
export function useRememberPages(): void {
  const { pathname } = useLocation();
  const principal = useSession().me?.id;
  useEffect(() => {
    if (principal === undefined || pathname === "/") {
      return;
    }
    const route = routeAt(manifests, pathname);
    if (route) {
      rememberPage(principal, { path: pathname, titleKey: route.titleKey, module: route.module, at: Date.now() });
    }
  }, [pathname, principal]);
}
