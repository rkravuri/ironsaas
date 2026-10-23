import { IconClockShield, IconLink, IconListSearch, IconLock } from "@tabler/icons-react";
import { gridShortcuts, tabShortcuts } from "@/components/shortcuts";
import type { ModuleManifest } from "@/modules/registry";
import { AuditChainPage } from "./pages/AuditChainPage";
import { AuditPage } from "./pages/AuditPage";
import { LegalHoldsPage } from "./pages/LegalHoldsPage";
import { RetentionPage } from "./pages/RetentionPage";

const read = "audit.events.read";

/**
 * The audit trail (kernel layer, ADR-0029; S3 T6, ADR-0050): its search with export and archive, the chain's status
 * with the fleet's verification, legal holds and retention. Every page reads with audit.events.read; placing a hold
 * needs audit.legal_holds.manage, and choosing a retention audit.retention.manage, each with a recent sign-in.
 */
export const auditManifest: ModuleManifest = {
  name: "audit",
  titleKey: "modules.audit.title",
  routes: [
    {
      path: "/audit",
      permission: read,
      element: <AuditPage />,
      titleKey: "modules.audit.search",
      shortcuts: [...gridShortcuts, ...tabShortcuts],
    },
    {
      path: "/audit/chain",
      permission: read,
      element: <AuditChainPage />,
      titleKey: "modules.audit.chain",
    },
    {
      path: "/audit/legal-holds",
      permission: read,
      element: <LegalHoldsPage />,
      titleKey: "modules.audit.legalHolds",
    },
    {
      path: "/audit/retention",
      permission: read,
      element: <RetentionPage />,
      titleKey: "modules.audit.retention",
    },
  ],
  navigation: [
    {
      labelKey: "modules.audit.search",
      path: "/audit",
      permission: read,
      icon: <IconListSearch size={16} />,
    },
    {
      labelKey: "modules.audit.chain",
      path: "/audit/chain",
      permission: read,
      icon: <IconLink size={16} />,
    },
    {
      labelKey: "modules.audit.legalHolds",
      path: "/audit/legal-holds",
      permission: read,
      icon: <IconLock size={16} />,
    },
    {
      labelKey: "modules.audit.retention",
      path: "/audit/retention",
      permission: read,
      icon: <IconClockShield size={16} />,
    },
  ],
  opener: { accent: "info", lineKey: "modules.audit.line" },
};
