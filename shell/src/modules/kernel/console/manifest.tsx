import { IconSitemap } from "@tabler/icons-react";
import type { ModuleManifest } from "@/modules/registry";
import { UnitsPage } from "./pages/UnitsPage";

/**
 * The tenant console (kernel layer, ADR-0029): the administration of the kernel's mechanisms. A1 brings the
 * organisation tree; roles, entitlements, flags and settings join it in later tasks of S2.
 */
export const consoleManifest: ModuleManifest = {
  name: "console",
  titleKey: "modules.console.title",
  routes: [{ path: "/console/units", permission: "authz.units.read", element: <UnitsPage />, titleKey: "modules.console.units" }],
  navigation: [{ labelKey: "modules.console.units", path: "/console/units", permission: "authz.units.read", icon: <IconSitemap size={16} /> }],
};
