import { IconCertificate, IconChecklist, IconFileZip } from "@tabler/icons-react";
import { gridShortcuts, tabShortcuts } from "@/components/shortcuts";
import type { ModuleManifest } from "@/modules/registry";
import { AuditReviewRecordPage } from "./pages/AuditReviewRecordPage";
import { AuditReviewsPage } from "./pages/AuditReviewsPage";
import { EvidencePackRecordPage } from "./pages/EvidencePackRecordPage";
import { EvidencePacksPage } from "./pages/EvidencePacksPage";
import { SignedSnapshotPage } from "./pages/SignedSnapshotPage";
import { ValidationPage } from "./pages/ValidationPage";

const reviewsRead = "compliance.audit_reviews.read";
const evidenceExport = "compliance.evidence.export";
const validationRead = "compliance.validation.read";

/**
 * Compliance (kernel layer, ADR-0029; S3 T7, ADR-0051): the periodic reviews of the audit trail, always signed, and the
 * snapshot a signature signed. A record's signatures are read by whoever may read the record, which the API decides, so
 * the snapshot's route asks only for a signed-in person. S3 T8 (ADR-0052) brought the evidence packs (TF-067), for the
 * holders of the high-risk compliance.evidence.export, and the validation pack of the installed version (TF-218).
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
      path: "/compliance/evidence-packs",
      permission: evidenceExport,
      element: <EvidencePacksPage />,
      titleKey: "modules.compliance.evidencePacks",
      shortcuts: gridShortcuts,
    },
    {
      path: "/compliance/evidence-packs/:id",
      permission: evidenceExport,
      element: <EvidencePackRecordPage />,
      titleKey: "modules.compliance.evidencePacks",
      shortcuts: tabShortcuts,
    },
    {
      path: "/compliance/validation",
      permission: validationRead,
      element: <ValidationPage />,
      titleKey: "modules.compliance.validation",
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
    {
      labelKey: "modules.compliance.evidencePacks",
      path: "/compliance/evidence-packs",
      permission: evidenceExport,
      icon: <IconFileZip size={16} />,
    },
    {
      labelKey: "modules.compliance.validation",
      path: "/compliance/validation",
      permission: validationRead,
      icon: <IconCertificate size={16} />,
    },
  ],
  opener: { accent: "success", lineKey: "modules.compliance.line" },
};
