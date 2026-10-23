import { IconChecklist } from "@tabler/icons-react";
import { gridShortcuts, tabShortcuts } from "@/components/shortcuts";
import type { ModuleManifest } from "@/modules/registry";
import { AuditReviewRecordPage } from "./pages/AuditReviewRecordPage";
import { AuditReviewsPage } from "./pages/AuditReviewsPage";
import { SignedSnapshotPage } from "./pages/SignedSnapshotPage";

const reviewsRead = "compliance.audit_reviews.read";

/**
 * Compliance (kernel layer, ADR-0029; S3 T7, ADR-0051): the periodic reviews of the audit trail, always signed, and the
 * snapshot a signature signed. A record's signatures are read by whoever may read the record, which the API decides, so
 * the snapshot's route asks only for a signed-in person.
 */
export const complianceManifest: ModuleManifest = {
  name: "compliance",
  titleKey: "modules.compliance.title",
  routes: [
    {
      path: "/compliance/audit-reviews",
      permission: reviewsRead,
      element: <AuditReviewsPage />,
      titleKey: "modules.compliance.auditReviews",
      shortcuts: gridShortcuts,
    },
    {
      path: "/compliance/audit-reviews/:id",
      permission: reviewsRead,
      element: <AuditReviewRecordPage />,
      titleKey: "modules.compliance.auditReviews",
      shortcuts: tabShortcuts,
    },
    {
      path: "/signatures/:id",
      permission: null,
      element: <SignedSnapshotPage />,
      titleKey: "modules.compliance.signedSnapshot",
      shortcuts: tabShortcuts,
    },
  ],
  navigation: [
    {
      labelKey: "modules.compliance.auditReviews",
      path: "/compliance/audit-reviews",
      permission: reviewsRead,
      icon: <IconChecklist size={16} />,
    },
  ],
  opener: { accent: "success", lineKey: "modules.compliance.line" },
};
