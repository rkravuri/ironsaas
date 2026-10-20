import { IconAdjustments, IconClipboardCheck, IconKey, IconReceipt, IconSitemap, IconSquarePlus, IconUserShare } from "@tabler/icons-react";
import { gridShortcuts, tabShortcuts, treeShortcuts } from "@/components/shortcuts";
import type { ModuleManifest } from "@/modules/registry";
import { DelegationsPage } from "./pages/DelegationsPage";
import { ReviewRecordPage } from "./pages/ReviewRecordPage";
import { ReviewsPage } from "./pages/ReviewsPage";
import { RoleEditorPage } from "./pages/RoleEditorPage";
import { RolesPage } from "./pages/RolesPage";
import { SettingsPage } from "./pages/SettingsPage";
import { SubscriptionPage } from "./pages/SubscriptionPage";
import { UnitsPage } from "./pages/UnitsPage";

/**
 * The tenant console (kernel layer, ADR-0029): the administration of the kernel's mechanisms. A1 brought the
 * organisation tree, A2 the roles, A4 the subscription (read only: plans are the operator's), A6 the settings of the
 * tenant and its units, A7 delegations (for everyone: anyone may hand their duties to someone) and access reviews (for
 * their reviewers, ADR-0037). Flag rules are the operator's, in the control plane (ADR-0035).
 */
export const consoleManifest: ModuleManifest = {
  name: "console",
  titleKey: "modules.console.title",
  routes: [
    { path: "/console/units", permission: "authz.units.read", element: <UnitsPage />, titleKey: "modules.console.units", shortcuts: treeShortcuts },
    { path: "/console/roles", permission: "authz.roles.read", element: <RolesPage />, titleKey: "modules.console.roles", shortcuts: gridShortcuts },
    { path: "/console/roles/new", permission: "authz.roles.manage", element: <RoleEditorPage />, titleKey: "modules.console.roles" },
    { path: "/console/roles/:id", permission: "authz.roles.read", element: <RoleEditorPage />, titleKey: "modules.console.roles" },
    { path: "/console/subscription", permission: "entitlements.subscription.read", element: <SubscriptionPage />, titleKey: "modules.console.subscription" },
    { path: "/console/settings", permission: "settings.values.read", element: <SettingsPage />, titleKey: "modules.console.settings" },
    { path: "/console/delegations", permission: null, element: <DelegationsPage />, titleKey: "modules.console.delegations" },
    { path: "/console/access-reviews", permission: "authz.reviews.decide", element: <ReviewsPage />, titleKey: "modules.console.accessReviews", shortcuts: gridShortcuts },
    { path: "/console/access-reviews/:id", permission: "authz.reviews.decide", element: <ReviewRecordPage />, titleKey: "modules.console.accessReviews", shortcuts: tabShortcuts },
  ],
  navigation: [
    { labelKey: "modules.console.units", path: "/console/units", permission: "authz.units.read", icon: <IconSitemap size={16} /> },
    { labelKey: "modules.console.roles", path: "/console/roles", permission: "authz.roles.read", icon: <IconKey size={16} /> },
    { labelKey: "modules.console.subscription", path: "/console/subscription", permission: "entitlements.subscription.read", icon: <IconReceipt size={16} /> },
    { labelKey: "modules.console.settings", path: "/console/settings", permission: "settings.values.read", icon: <IconAdjustments size={16} /> },
    { labelKey: "modules.console.delegations", path: "/console/delegations", permission: null, icon: <IconUserShare size={16} /> },
    { labelKey: "modules.console.accessReviews", path: "/console/access-reviews", permission: "authz.reviews.decide", icon: <IconClipboardCheck size={16} /> },
  ],
  opener: { accent: "console", lineKey: "modules.console.line" },
  // The Units page opens its dialog for a new unit under the person's home unit from ?add (D4).
  quickActions: [{ id: "add-unit", labelKey: "units.quick.add", permission: "authz.units.manage", path: "/console/units?add=1", icon: <IconSquarePlus size={16} /> }],
};
