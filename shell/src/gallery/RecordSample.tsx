import { useTranslation } from "react-i18next";
import { Button, Stack, Table } from "@mantine/core";
import { ApprovalPanel, AttachmentPanel, CommentPanel, HistoryPanel } from "@/components/panels";
import { RecordPage } from "@/components/templates";

/** The sample record page: every panel behind its tab, on a made-up order. */
export function RecordSample() {
  const { t } = useTranslation();
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
              <Table variant="vertical">
                <Table.Tbody>
                  {[
                    [t("gallery.record.customer"), "Orbit Logistics"],
                    [t("gallery.record.total"), "12,480.00"],
                    [t("gallery.record.due"), new Date(at(28)).toLocaleDateString()],
                  ].map(([label, value]) => (
                    <Table.Tr key={label}>
                      <Table.Th w={180}>{label}</Table.Th>
                      <Table.Td>{value}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
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
                  { id: "h2", at: at(13), actor: "Ada Lovelace", action: t("gallery.record.events.changed"), summary: t("gallery.record.events.changedSummary") },
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
                <CommentPanel comments={[{ id: "c1", author: "Grace Hopper", at: at(14), text: t("gallery.record.comment") }]} />
              </Stack>
            ),
          },
        ]}
      />
    </div>
  );
}
