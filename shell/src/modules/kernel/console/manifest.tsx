import {
  IconAdjustments,
  IconClipboardCheck,
  IconDevices,
  IconFileCertificate,
  IconHelpCircle,
  IconKey,
  IconLogin2,
  IconReceipt,
  IconScale,
  IconSitemap,
  IconSquarePlus,
  IconUserShare,
} from "@tabler/icons-react";
import { gridShortcuts, tabShortcuts, treeShortcuts } from "@/components/shortcuts";
import type { ModuleManifest } from "@/modules/registry";
import { ConflictsPage } from "./pages/ConflictsPage";
import { DelegationsPage } from "./pages/DelegationsPage";
import { DocumentsPage } from "./pages/DocumentsPage";
import { ExplainPage } from "./pages/ExplainPage";
import { ReviewRecordPage } from "./pages/ReviewRecordPage";
import { ReviewsPage } from "./pages/ReviewsPage";
import { RoleEditorPage } from "./pages/RoleEditorPage";
import { RolesPage } from "./pages/RolesPage";
import { SessionsPage } from "./pages/SessionsPage";
import { SettingsPage } from "./pages/SettingsPage";
import { SsoProviderPage } from "./pages/SsoProviderPage";
import { SsoProvidersPage } from "./pages/SsoProvidersPage";
import { SubscriptionPage } from "./pages/SubscriptionPage";
import { UnitsPage } from "./pages/UnitsPage";

/**
 * The tenant console (kernel layer, ADR-0029): the administration of the kernel's mechanisms. A1 brought the
 * organisation tree, A2 the roles, A4 the subscription (read only: plans are the operator's), A6 the settings of the
 * tenant and its units, A7 others' delegations (for their administrators; a person's own are in My profile) and access
 * reviews (for their reviewers, ADR-0037), A9 the explanation of a person's access and the conflict rules with their
 * report (ADR-0039). Flag rules are the operator's, in the control plane (ADR-0035). S3 T4 brought the company logins
 * (TF-014, ADR-0048), for the holders of the critical identity.sso.manage. S3 T5 brought the sessions, with the summary and
 * the tenant-wide revocation (TF-022, TF-029), and the documents people accept (TF-028, ADR-0049).
 */
export const consoleManifest: ModuleManifest = {
  name: "console",
  titleKey: "modules.console.title",
  routes: [
    {
      path: "/console/units",
      permission: "authz.units.read",
      element: <UnitsPage />,
      titleKey: "modules.console.units",
      shortcuts: treeShortcuts,
    },
    {
      path: "/console/roles",
      permission: "authz.roles.read",
      element: <RolesPage />,
      titleKey: "modules.console.roles",
      shortcuts: gridShortcuts,
    },
    {
      path: "/console/roles/new",
      permission: "authz.roles.manage",
      element: <RoleEditorPage />,
      titleKey: "modules.console.roles",
    },
    {
      path: "/console/roles/:id",
      permission: "authz.roles.read",
      element: <RoleEditorPage />,
      titleKey: "modules.console.roles",
    },
    {
      path: "/console/subscription",
      permission: "entitlements.subscription.read",
      element: <SubscriptionPage />,
      titleKey: "modules.console.subscription",
    },
    {
      path: "/console/settings",
      permission: "settings.values.read",
      element: <SettingsPage />,
      titleKey: "modules.console.settings",
    },
    {
      path: "/console/delegations",
      permission: "authz.delegations.manage",
      element: <DelegationsPage />,
      titleKey: "modules.console.delegations",
      shortcuts: gridShortcuts,
    },
    {
      path: "/console/access-reviews",
      permission: "authz.reviews.decide",
      element: <ReviewsPage />,
      titleKey: "modules.console.accessReviews",
      shortcuts: gridShortcuts,
    },
    {
      path: "/console/access-reviews/:id",
      permission: "authz.reviews.decide",
      element: <ReviewRecordPage />,
      titleKey: "modules.console.accessReviews",
      shortcuts: tabShortcuts,
    },
    {
      path: "/console/explain",
      permission: "authz.access.explain",
      element: <ExplainPage />,
      titleKey: "modules.console.explain",
    },
    {
      path: "/console/conflicts",
      permission: "authz.sod.read",
      element: <ConflictsPage />,
      titleKey: "modules.console.conflicts",
      shortcuts: tabShortcuts,
    },
    {
      path: "/console/sso",
      permission: "identity.sso.manage",
      element: <SsoProvidersPage />,
      titleKey: "modules.console.sso",
      shortcuts: gridShortcuts,
    },
    {
      path: "/console/sessions",
      permission: "identity.sessions.manage",
      element: <SessionsPage />,
      titleKey: "modules.console.sessions",
      shortcuts: gridShortcuts,
    },
    {
      path: "/console/documents",
      permission: "identity.documents.manage",
      element: <DocumentsPage />,
      titleKey: "modules.console.documents",
      shortcuts: gridShortcuts,
    },
    {
      path: "/console/sso/new",
      permission: "identity.sso.manage",
      element: <SsoProviderPage />,
      titleKey: "modules.console.sso",
    },
    {
      path: "/console/sso/:id",
      permission: "identity.sso.manage",
      element: <SsoProviderPage />,
      titleKey: "modules.console.sso",
    },
  ],
  navigation: [
    {
      labelKey: "modules.console.units",
      path: "/console/units",
      permission: "authz.units.read",
      icon: <IconSitemap size={16} />,
    },
    {
      labelKey: "modules.console.roles",
      path: "/console/roles",
      permission: "authz.roles.read",
      icon: <IconKey size={16} />,
    },
    {
      labelKey: "modules.console.subscription",
      path: "/console/subscription",
      permission: "entitlements.subscription.read",
      icon: <IconReceipt size={16} />,
    },
    {
      labelKey: "modules.console.settings",
      path: "/console/settings",
      permission: "settings.values.read",
      icon: <IconAdjustments size={16} />,
    },
    {
      labelKey: "modules.console.delegations",
      path: "/console/delegations",
      permission: "authz.delegations.manage",
      icon: <IconUserShare size={16} />,
    },
    {
      labelKey: "modules.console.accessReviews",
      path: "/console/access-reviews",
      permission: "authz.reviews.decide",
      icon: <IconClipboardCheck size={16} />,
    },
    {
      labelKey: "modules.console.explain",
      path: "/console/explain",
      permission: "authz.access.explain",
      icon: <IconHelpCircle size={16} />,
    },
    {
      labelKey: "modules.console.conflicts",
      path: "/console/conflicts",
      permission: "authz.sod.read",
      icon: <IconScale size={16} />,
    },
    {
      labelKey: "modules.console.sso",
      path: "/console/sso",
      permission: "identity.sso.manage",
      icon: <IconLogin2 size={16} />,
    },
    {
      labelKey: "modules.console.sessions",
      path: "/console/sessions",
      permission: "identity.sessions.manage",
      icon: <IconDevices size={16} />,
    },
    {
      labelKey: "modules.console.documents",
      path: "/console/documents",
      permission: "identity.documents.manage",
      icon: <IconFileCertificate size={16} />,
    },
  ],
  opener: { accent: "console", lineKey: "modules.console.line" },
  // The Units page opens its dialog for a new unit under the person's home unit from ?add (D4).
  quickActions: [
    {
      id: "add-unit",
      labelKey: "units.quick.add",
      permission: "authz.units.manage",
      path: "/console/units?add=1",
      icon: <IconSquarePlus size={16} />,
    },
  ],
};
