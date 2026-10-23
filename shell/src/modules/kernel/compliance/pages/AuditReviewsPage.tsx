import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Badge, Box, Button, Group, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCircleCheck, IconPlus } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import {
  complianceQueryKey,
  fetchAuditReviews,
  fetchReviewCoverage,
  recordAuditReview,
  type AuditReviewInput,
  type AuditReviewRow,
} from "@/api/compliance";
import { useSession } from "@/auth/session-context";
import { SigningDialog } from "@/auth/SigningDialog";
import { useResumedSigning } from "@/auth/signing-resume";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { FormDialog, LoadingState } from "@/components/panels";
import { ListPage } from "@/components/templates";
import { Mandala } from "@/design/patterns";
import { formatDateTime } from "@/i18n/dates";
import { RecordReviewForm } from "./RecordReviewForm";
import { monthWords, outcomeWords, periodWords } from "./review-words";

const record = "compliance.audit_reviews.record";
const defaultSort = [{ field: "period_from", direction: "desc" as const }];
const months = 12;

/**
 * The reviews of the audit trail (TF-219 AC3, ADR-0051) on the list template: the last twelve months, each with the
 * reviews that cover it, so a month nobody reviewed shows in words; then every review, newest period first. Recording
 * one is a form, then the signature with the meaning "reviewed": a review is always signed.
 */
export function AuditReviewsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const session = useSession();
  const canRecord = session.tenantPermissions.includes(record);
  const [writing, setWriting] = useState(false);
  // Back from a company login's fresh sign-in, the review waiting for its signature opens again (ADR-0051).
  const resumed = useResumedSigning("audit_trail_review");
  const [pending, setPending] = useState<AuditReviewInput | undefined>(
    () => resumed?.context as AuditReviewInput | undefined,
  );
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchAuditReviews(query, signal), []);
  const columns: DataGridColumn<AuditReviewRow>[] = [
    {
      field: "period_from",
      headerKey: "auditReviews.columns.period",
      sortable: true,
      flex: 2,
      minWidth: 240,
      format: (row) => periodWords(row.period_from, row.period_to, t),
    },
    {
      field: "outcome",
      headerKey: "auditReviews.columns.outcome",
      width: 160,
      filter: "text",
      format: (row) => outcomeWords(row.outcome, t),
    },
    { field: "reviewer_name", headerKey: "auditReviews.columns.reviewer", flex: 1, minWidth: 180 },
    {
      field: "recorded_at",
      headerKey: "auditReviews.columns.recorded",
      sortable: true,
      width: 235,
      format: (row) => formatDateTime(row.recorded_at),
    },
  ];

  return (
    <>
      <ListPage
        title={t("auditReviews.title")}
        description={t("auditReviews.description")}
        drawing={
          <Mandala symmetry={6} rings={2} stroke={2} density={0.5} seed="audit-reviews" size={56} accent="success" />
        }
        actions={
          canRecord && (
            <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={() => setWriting(true)}>
              {t("auditReviews.record.open")}
            </Button>
          )
        }
      >
        <Stack gap="md" h="100%">
          <Coverage />
          <Box style={{ flex: 1, minHeight: 280 }}>
            <DataGrid<AuditReviewRow>
              columns={columns}
              fetchPage={fetchPage}
              defaultSort={defaultSort}
              label={t("auditReviews.gridLabel")}
              onRowOpen={(row) => void navigate(`/compliance/audit-reviews/${row.id}`)}
            />
          </Box>
        </Stack>
      </ListPage>
      <FormDialog opened={writing} title={t("auditReviews.record.title")} onClose={() => setWriting(false)}>
        {writing && (
          <RecordReviewForm
            onClose={() => setWriting(false)}
            onContinue={(input) => {
              setWriting(false);
              setPending(input);
            }}
          />
        )}
      </FormDialog>
      <SigningDialog
        opened={pending !== undefined}
        recordType="audit_trail_review"
        title={t("auditReviews.sign.title")}
        summary={
          pending &&
          t("auditReviews.sign.summary", {
            period: periodWords(pending.period_from, pending.period_to, t),
            outcome: outcomeWords(pending.outcome, t),
          })
        }
        meanings={["reviewed"]}
        comment={false}
        context={pending}
        initial={resumed}
        onClose={() => setPending(undefined)}
        onSign={async (credentials) => {
          if (pending) {
            const review = await recordAuditReview(pending, credentials);
            setPending(undefined);
            await queryClient.invalidateQueries({ queryKey: complianceQueryKey });
            await navigate(`/compliance/audit-reviews/${review.id}`);
          }
        }}
      />
    </>
  );
}

/** The last months, newest first: reviewed, with its outcomes, or not reviewed, in words and a mark. */
function Coverage() {
  const { t } = useTranslation();
  const coverage = useQuery({
    queryKey: [...complianceQueryKey, "coverage", months],
    queryFn: ({ signal }) => fetchReviewCoverage(months, signal),
  });
  return (
    <Stack gap={6} component="section" aria-labelledby="review-coverage-title">
      <Title order={2} size="h5" id="review-coverage-title">
        {t("auditReviews.coverage.title")}
      </Title>
      {coverage.isPending ? (
        <LoadingState height={48} />
      ) : coverage.isError ? (
        <Alert color="danger" role="alert">
          {coverage.error instanceof ApiError ? coverage.error.message : t("errors.unexpected")}
        </Alert>
      ) : (
        <Group gap={6} component="ul" m={0} pl={0} style={{ listStyle: "none" }} data-coverage>
          {coverage.data.months.map((month) => {
            const reviewed = month.reviews.length > 0;
            return (
              <li key={month.from} data-month={month.from} data-reviewed={reviewed}>
                <Badge
                  variant={reviewed ? "light" : "outline"}
                  color={reviewed ? "success" : "warning"}
                  size="lg"
                  radius="sm"
                  tt="none"
                  leftSection={
                    reviewed ? (
                      <IconCircleCheck size={14} aria-hidden="true" />
                    ) : (
                      <IconAlertTriangle size={14} aria-hidden="true" />
                    )
                  }
                >
                  {t(reviewed ? "auditReviews.coverage.reviewed" : "auditReviews.coverage.missing", {
                    month: monthWords(month.from),
                    count: month.reviews.length,
                  })}
                </Badge>
              </li>
            );
          })}
        </Group>
      )}
      <Text size="xs" c="dimmed">
        {t("auditReviews.coverage.hint")}
      </Text>
    </Stack>
  );
}
