import { IconKey, IconSitemap, IconSquarePlus } from "@tabler/icons-react";
import { gridShortcuts, treeShortcuts } from "@/components/shortcuts";
import type { ModuleManifest } from "@/modules/registry";
import { RoleEditorPage } from "./pages/RoleEditorPage";
import { RolesPage } from "./pages/RolesPage";
import { UnitsPage } from "./pages/UnitsPage";

/**
 * The tenant console (kernel layer, ADR-0029): the administration of the kernel's mechanisms. A1 brought the
 * organisation tree, A2 the roles; entitlements, flags and settings join it in later tasks of S2.
 */
export const consoleManifest: ModuleManifest = {
  name: "console",
  titleKey: "modules.console.title",
  routes: [
    { path: "/console/units", permission: "authz.units.read", element: <UnitsPage />, titleKey: "modules.console.units", shortcuts: treeShortcuts },
    { path: "/console/roles", permission: "authz.roles.read", element: <RolesPage />, titleKey: "modules.console.roles", shortcuts: gridShortcuts },
    { path: "/console/roles/new", permission: "authz.roles.manage", element: <RoleEditorPage />, titleKey: "modules.console.roles" },
    { path: "/console/roles/:id", permission: "authz.roles.read", element: <RoleEditorPage />, titleKey: "modules.console.roles" },
  ],
  navigation: [
    { labelKey: "modules.console.units", path: "/console/units", permission: "authz.units.read", icon: <IconSitemap size={16} /> },
    { labelKey: "modules.console.roles", path: "/console/roles", permission: "authz.roles.read", icon: <IconKey size={16} /> },
  ],
  opener: { accent: "console", lineKey: "modules.console.line" },
  // The Units page opens its dialog for a new unit under the person's home unit from ?add (D4).
  quickActions: [{ id: "add-unit", labelKey: "units.quick.add", permission: "authz.units.manage", path: "/console/units?add=1", icon: <IconSquarePlus size={16} /> }],
};
