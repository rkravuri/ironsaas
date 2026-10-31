import {
  IconAdjustments,
  IconAlertTriangle,
  IconBellCog,
  IconClockPlay,
  IconClipboardCheck,
  IconDevices,
  IconFileCertificate,
  IconHelpCircle,
  IconInboxOff,
  IconKey,
  IconLogin2,
  IconReceipt,
  IconScale,
  IconShieldLock,
  IconSitemap,
  IconSquarePlus,
  IconTags,
  IconTimelineEvent,
  IconUserShare,
  IconUsersGroup,
} from "@tabler/icons-react";
import { gridShortcuts, tabShortcuts, treeShortcuts } from "@/components/shortcuts";
import type { ModuleManifest } from "@/modules/registry";
import { AlertRulesPage } from "./pages/AlertRulesPage";
import { ChangeRecordPage } from "./pages/ChangeRecordPage";
import { ChangesPage } from "./pages/ChangesPage";
import { ConflictsPage } from "./pages/ConflictsPage";
import { DeadLettersPage } from "./pages/DeadLettersPage";
import { DelegationsPage } from "./pages/DelegationsPage";
import { DocumentsPage } from "./pages/DocumentsPage";
import { readEvents } from "./pages/event-filters";
import { EventsPage } from "./pages/EventsPage";
import { ExplainPage } from "./pages/ExplainPage";
import { readJobs } from "./pages/job-words";
import { JobsPage } from "./pages/JobsPage";
import { KeysPage } from "./pages/KeysPage";
import { ReviewRecordPage } from "./pages/ReviewRecordPage";
import { ReviewsPage } from "./pages/ReviewsPage";
import { SecurityAlertsPage } from "./pages/SecurityAlertsPage";
import { RoleEditorPage } from "./pages/RoleEditorPage";
import { RolesPage } from "./pages/RolesPage";
import { SessionsPage } from "./pages/SessionsPage";
import { SettingsPage } from "./pages/SettingsPage";
import { SsoProviderPage } from "./pages/SsoProviderPage";
import { SsoProvidersPage } from "./pages/SsoProvidersPage";
import { SubscriptionPage } from "./pages/SubscriptionPage";
import { UnitsPage } from "./pages/UnitsPage";
import { VisibilityPage } from "./pages/VisibilityPage";

/**
 * The tenant console (kernel layer, ADR-0029): the administration of the kernel's mechanisms. A1 brought the
 * organisation tree, A2 the roles, A4 the subscription (read only: plans are the operator's), A6 the settings of the
 * tenant and its units, A7 others' delegations (for their administrators; a person's own are in My profile) and access
 * reviews (for their reviewers, ADR-0037), A9 the explanation of a person's access and the conflict rules with their
 * report (ADR-0039). Flag rules are the operator's, in the control plane (ADR-0035). S3 T4 brought the company logins
 * (TF-014, ADR-0048), for the holders of the critical identity.sso.manage. S3 T5 brought the sessions, with the summary and
 * the tenant-wide revocation (TF-022, TF-029), and the documents people accept (TF-028, ADR-0049). S3 T8 brought the
 * changes of sensitive configuration that wait for a second administrator (TF-068, ADR-0052). S3 T9b brought the keys, read only:
 * their versions, rotations and re-encryption; rotating them is the operator's (TF-075, ADR-0054). V1 brought content
 * visibility: the tenant's visibility tags and who holds them (TF-241, ADR-0057); people get theirs on their record.
 * S4 T1 brought the domain events, their subscribers and dead letters, and their replay (TF-112, ADR-0060). S4 T2 brought
 * the jobs, paused, resumed, started and run again with a reason, and one view of the dead letters of jobs and events
 * (TF-121, TF-123, ADR-0061).
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
      path: "/console/visibility",
      permission: "visibility.tags.read",
      element: <VisibilityPage />,
      titleKey: "modules.console.visibility",
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
    // Whoever asked for a change, or may decide one, reaches it from the frame's notice or a link; the API decides which
    // changes each sees (ADR-0052), so the routes ask only for a signed-in person.
    {
      path: "/console/changes",
      permission: null,
      element: <ChangesPage />,
      titleKey: "modules.console.changes",
      shortcuts: gridShortcuts,
    },
    {
      path: "/console/changes/:id",
      permission: null,
      element: <ChangeRecordPage />,
      titleKey: "modules.console.changes",
      shortcuts: tabShortcuts,
    },
    {
      path: "/console/keys",
      permission: "keys.keys.read",
      element: <KeysPage />,
      titleKey: "modules.console.keys",
    },
    {
      path: "/console/security-alerts",
      permission: "monitoring.alerts.read",
      element: <SecurityAlertsPage />,
      titleKey: "modules.console.securityAlerts",
      shortcuts: gridShortcuts,
    },
    {
      path: "/console/alert-rules",
      permission: "monitoring.alerts.read",
      element: <AlertRulesPage />,
      titleKey: "modules.console.alertRules",
    },
    {
      path: "/console/events",
      permission: readEvents,
      element: <EventsPage />,
      titleKey: "modules.console.events",
      shortcuts: [...gridShortcuts, ...tabShortcuts],
    },
    {
      path: "/console/jobs",
      permission: readJobs,
      element: <JobsPage />,
      titleKey: "modules.console.jobs",
      shortcuts: gridShortcuts,
    },
    {
      path: "/console/dead-letters",
      permission: readJobs,
      element: <DeadLettersPage />,
      titleKey: "modules.console.deadLetters",
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
      labelKey: "modules.console.visibility",
      path: "/console/visibility",
      permission: "visibility.tags.read",
      icon: <IconTags size={16} />,
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
    {
      labelKey: "modules.console.changes",
      path: "/console/changes",
      permission: "changes.changes.read",
      icon: <IconUsersGroup size={16} />,
    },
    {
      labelKey: "modules.console.keys",
      path: "/console/keys",
      permission: "keys.keys.read",
      icon: <IconShieldLock size={16} />,
    },
    {
      labelKey: "modules.console.securityAlerts",
      path: "/console/security-alerts",
      permission: "monitoring.alerts.read",
      icon: <IconAlertTriangle size={16} />,
    },
    {
      labelKey: "modules.console.alertRules",
      path: "/console/alert-rules",
      permission: "monitoring.alerts.read",
      icon: <IconBellCog size={16} />,
    },
    {
      labelKey: "modules.console.events",
      path: "/console/events",
      permission: readEvents,
      icon: <IconTimelineEvent size={16} />,
    },
    {
      labelKey: "modules.console.jobs",
      path: "/console/jobs",
      permission: readJobs,
      icon: <IconClockPlay size={16} />,
    },
    {
      labelKey: "modules.console.deadLetters",
      path: "/console/dead-letters",
      permission: readJobs,
      icon: <IconInboxOff size={16} />,
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
