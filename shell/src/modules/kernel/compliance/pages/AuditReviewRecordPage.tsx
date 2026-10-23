import { useTranslation } from "react-i18next";
import { Link, useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Alert, Anchor, Stack, Table, Text } from "@mantine/core";
import { ApiError } from "@/api/client";
import { fetchAuditEventTypes } from "@/api/audit";
import { fetchUnits } from "@/api/authorization";
import { complianceQueryKey, fetchAuditReview, type AuditReview } from "@/api/compliance";
import { useHistoryOf } from "@/auth/record-history";
import { LoadingState, type RecordStatus } from "@/components/panels";
import { RecordPage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";
import { outcomeWords, periodWords } from "./review-words";

/**
 * One review of the audit trail (TF-219 AC3) on the record template: its period, the reviewer and the outcome in the
 * header, its signature block under it, on screen and in print (TF-219 AC1), and what it covered: the units, the event
 * types, the notes and the searches the reviewer ran, each a link back into the audit trail.
 */
export function AuditReviewRecordPage() {
  const { t } = useTranslation();
  const { id = "" } = useParams();
  const history = useHistoryOf("audit_trail_review", id === "" ? undefined : id);
  const review = useQuery({
    queryKey: [...complianceQueryKey, "audit-reviews", id],
    queryFn: ({ signal }) => fetchAuditReview(id, signal),
  });
  if (review.isPending) {
    return <LoadingState height={240} />;
  }
  if (review.isError) {
    return (
      <Alert color="danger" role="alert" title={t("auditReviews.failed")}>
        {review.error instanceof ApiError ? review.error.message : t("errors.unexpected")}
        {review.error instanceof ApiError && review.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: review.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }

  const data = review.data;
  const status: RecordStatus =
    data.outcome === "findings"
      ? { label: outcomeWords(data.outcome, t), tone: "warning" }
      : { label: outcomeWords(data.outcome, t), tone: "success" };
  return (
    <RecordPage
      header={{
        title: t("auditReviews.record.heading", { period: periodWords(data.period_from, data.period_to, t) }),
        status,
        meta: [
          { label: t("auditReviews.record.reviewer"), value: data.reviewer_name },
          { label: t("auditReviews.record.recorded"), value: formatDateTime(data.recorded_at) },
        ],
      }}
      signatures={{ recordType: "audit_trail_review", recordId: data.id }}
      tabs={[{ value: "review", label: t("auditReviews.record.tab"), panel: <Covered review={data} /> }]}
      history={history}
    />
  );
}

function Covered({ review }: { review: AuditReview }) {
  const { t } = useTranslation();
  const units = useQuery({
    queryKey: ["authz", "units"],
    queryFn: ({ signal }) => fetchUnits(signal),
    enabled: review.unit_ids.length > 0,
  });
  const types = useQuery({
    queryKey: ["audit", "types"],
    queryFn: ({ signal }) => fetchAuditEventTypes(signal),
    enabled: review.event_types.length > 0,
  });
  const unitNames = review.unit_ids.map((id) => units.data?.data.find((unit) => unit.id === id)?.name ?? id);
  const typeNames = review.event_types.map((code) => types.data?.data.find((type) => type.code === code)?.name ?? code);
  return (
    <Stack gap="md">
      <Table withTableBorder variant="vertical" layout="fixed" data-review-scope>
        <Table.Tbody>
          <Table.Tr>
            <Table.Th w={200}>{t("auditReviews.record.period")}</Table.Th>
            <Table.Td>{periodWords(review.period_from, review.period_to, t)}</Table.Td>
          </Table.Tr>
          <Table.Tr>
            <Table.Th>{t("auditReviews.record.units")}</Table.Th>
            <Table.Td>{unitNames.length === 0 ? t("auditReviews.record.allUnits") : unitNames.join(", ")}</Table.Td>
          </Table.Tr>
          <Table.Tr>
            <Table.Th>{t("auditReviews.record.eventTypes")}</Table.Th>
            <Table.Td>{typeNames.length === 0 ? t("auditReviews.record.allTypes") : typeNames.join(", ")}</Table.Td>
          </Table.Tr>
          <Table.Tr>
            <Table.Th>{t("auditReviews.record.outcome")}</Table.Th>
            <Table.Td>{outcomeWords(review.outcome, t)}</Table.Td>
          </Table.Tr>
          <Table.Tr>
            <Table.Th>{t("auditReviews.record.notes")}</Table.Th>
            <Table.Td style={{ whiteSpace: "pre-wrap" }}>{review.notes ?? t("auditReviews.record.noNotes")}</Table.Td>
          </Table.Tr>
        </Table.Tbody>
      </Table>
      <Stack gap={4}>
        <Text fw={600}>{t("auditReviews.record.searches")}</Text>
        {review.searches.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t("auditReviews.record.noSearches")}
          </Text>
        ) : (
          <Stack gap={2} component="ul" m={0} pl="md">
            {review.searches.map((search) => (
              <li key={search.link + search.label}>
                <Anchor component={Link} to={search.link} size="sm">
                  {search.label}
                </Anchor>
              </li>
            ))}
          </Stack>
        )}
      </Stack>
    </Stack>
  );
}
