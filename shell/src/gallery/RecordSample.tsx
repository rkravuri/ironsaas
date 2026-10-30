import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Divider, Stack, Table } from "@mantine/core";
import type { RecordSharing } from "@/api/authorization";
import type { HeldTags } from "@/api/visibility";
import { ApprovalPanel, AttachmentPanel, CommentPanel, HistoryPanel } from "@/components/panels";
import { SharingPanel } from "@/components/sharing";
import { TagsPanel } from "@/components/visibility";
import { RecordPage } from "@/components/templates";
import { formatDate } from "@/i18n/dates";

const unit = (n: number, name: string, parent: number | null, depth: number) => ({
  id: `01a18000-0000-7000-8000-${String(n).padStart(12, "0")}`,
  name,
  code: name.toLowerCase(),
  parent_id: parent === null ? null : `01a18000-0000-7000-8000-${String(parent).padStart(12, "0")}`,
  depth,
});
const tree = [
  unit(1, "Acme", null, 0),
  unit(2, "Sales", 1, 1),
  unit(3, "North", 2, 2),
  unit(4, "South", 2, 2),
  unit(5, "Finance", 1, 1),
  unit(6, "Operations", 1, 1),
];
const named = (choice: (typeof tree)[number]) => ({ id: choice.id, name: choice.name, code: choice.code });

/** The sample order's sharing: North's, shared with Finance, changed here in memory only. */
const sampleSharing: RecordSharing = {
  record_type: "sales_order",
  record_id: "01a18000-0000-7000-8000-000000001042",
  owning_unit: named(tree[2]!),
  mode: "shared",
  units: [named(tree[4]!)],
  may_change: true,
  choices: tree,
};

/** The sample order's visibility tags (ADR-0057): one active, one retired, and the tags the reader may put on it. */
const sampleTags: HeldTags = {
  tags: [
    { code: "APAC", name: "Asia Pacific", active: true },
    { code: "PROJECT-7", name: "Project Seven", active: false },
  ],
  may_change: true,
  choices: [
    { code: "APAC", name: "Asia Pacific", active: true },
    { code: "EMEA", name: "Europe, Middle East and Africa", active: true },
  ],
};

/** The sample record page: every panel behind its tab, on a made-up order. */
export function RecordSample() {
  const { t } = useTranslation();
  const [sharing, setSharing] = useState(sampleSharing);
  const [tags, setTags] = useState(sampleTags);
  const at = (day: number) => new Date(Date.UTC(2026, 9, day, 9, 30)).toISOString();
  return (
    <div id="gallery-record-page">
      <RecordPage
        header={{
          title: t("gallery.record.title"),
          code: "ORD-1042",
          status: { label: t("gallery.record.status"), tone: "warning" },
          meta: [
            { label: t("gallery.record.owner"), value: "Ada Lovelace" },
            { label: t("gallery.record.unit"), value: "Sales, North" },
          ],
          actions: (
            <>
              <Button variant="default">{t("gallery.record.edit")}</Button>
              <Button>{t("gallery.record.submit")}</Button>
            </>
          ),
        }}
        tabs={[
          {
            value: "details",
            label: t("gallery.record.tabs.details"),
            panel: (
              <>
                <Table variant="vertical">
                  <Table.Tbody>
                    {[
                      [t("gallery.record.customer"), "Orbit Logistics"],
                      [t("gallery.record.total"), "12,480.00"],
                      [t("gallery.record.due"), formatDate(at(28))],
                    ].map(([label, value]) => (
                      <Table.Tr key={label}>
                        <Table.Th w={180}>{label}</Table.Th>
                        <Table.Td>{value}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
                <Divider my="md" />
                <SharingPanel
                  sharing={sharing}
                  onChange={(mode, units) => {
                    setSharing({
                      ...sharing,
                      mode,
                      units: tree.filter((choice) => units.includes(choice.id)).map(named),
                    });
                    return Promise.resolve();
                  }}
                />
                <Divider my="md" />
                <TagsPanel
                  value={tags}
                  label={t("visibility.control.label")}
                  noneWords={t("visibility.control.none")}
                  hint={t("visibility.control.hint")}
                  dialogTitle={t("visibility.control.title")}
                  onChange={(codes) => {
                    const known = [...sampleTags.tags, ...sampleTags.choices];
                    setTags({
                      ...tags,
                      tags: known.filter(
                        (tag, index) =>
                          codes.includes(tag.code) && known.findIndex((other) => other.code === tag.code) === index,
                      ),
                    });
                    return Promise.resolve();
                  }}
                />
              </>
            ),
          },
          {
            value: "attachments",
            label: t("gallery.record.tabs.attachments"),
            panel: (
              <AttachmentPanel
                attachments={[
                  { id: "a1", name: "order-1042.pdf", size: 182_000, uploadedAt: at(12), uploadedBy: "Ada Lovelace" },
                  { id: "a2", name: "delivery-note.pdf", size: 61_000, uploadedAt: at(14), uploadedBy: "Grace Hopper" },
                ]}
                onUpload={() => Promise.resolve()}
                onDownload={() => undefined}
              />
            ),
          },
          {
            value: "history",
            label: t("gallery.record.tabs.history"),
            panel: (
              <HistoryPanel
                events={[
                  { id: "h3", at: at(14), actor: "Grace Hopper", action: t("gallery.record.events.submitted") },
                  {
                    id: "h2",
                    at: at(13),
                    actor: "Ada Lovelace",
                    action: t("gallery.record.events.changed"),
                    summary: t("gallery.record.events.changedSummary"),
                  },
                  { id: "h1", at: at(12), actor: "Ada Lovelace", action: t("gallery.record.events.created") },
                ]}
              />
            ),
          },
          {
            value: "approval",
            label: t("gallery.record.tabs.approval"),
            panel: (
              <ApprovalPanel
                steps={[
                  { id: "s1", name: t("gallery.record.steps.manager"), approver: "Grace Hopper", state: "approved" },
                  { id: "s2", name: t("gallery.record.steps.finance"), approver: "Linus Pauling", state: "pending" },
                  { id: "s3", name: t("gallery.record.steps.director"), approver: "Marie Curie", state: "waiting" },
                ]}
              />
            ),
          },
          {
            value: "comments",
            label: t("gallery.record.tabs.comments"),
            panel: (
              <Stack>
                <CommentPanel
                  comments={[{ id: "c1", author: "Grace Hopper", at: at(14), text: t("gallery.record.comment") }]}
                />
              </Stack>
            ),
          },
        ]}
      />
    </div>
  );
}
