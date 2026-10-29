import { useTranslation } from "react-i18next";
import { Link, useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Table, Text } from "@mantine/core";
import { ApiError } from "@/api/client";
import { fetchSampleRecord, sampleRecordType, type SampleRecord } from "@/api/samples";
import { useHistoryOf } from "@/auth/record-history";
import { EmptyState, LoadingState } from "@/components/panels";
import { SharingControl } from "@/components/sharing";
import { RecordPage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";

/**
 * One sample record on the record template (ADR-0057): its visibility tags under the header, its sharing and details
 * behind tabs, and its History. A record outside the reader's units or tags answers not found, as one that does not
 * exist does (ADR-0032).
 */
export function SampleRecordPage() {
  const { t } = useTranslation();
  const { id = "" } = useParams();
  const record = useQuery({
    queryKey: ["samples", "records", id],
    queryFn: ({ signal }) => fetchSampleRecord(id, signal),
  });
  if (record.isPending) {
    return <LoadingState />;
  }
  if (record.isError) {
    if (record.error instanceof ApiError && record.error.status === 404) {
      return (
        <EmptyState
          drawing="nothingFound"
          title={t("samples.notFoundTitle")}
          text={t("samples.notFoundText")}
          action={
            <Button component={Link} to="/samples" variant="default">
              {t("samples.backToList")}
            </Button>
          }
        />
      );
    }
    return (
      <Alert color="danger" role="alert" title={t("samples.failed")}>
        {record.error instanceof ApiError ? record.error.message : t("errors.unexpected")}
        {record.error instanceof ApiError && record.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: record.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }
  return <SampleRecordView record={record.data} />;
}

function SampleRecordView({ record }: { record: SampleRecord }) {
  const { t } = useTranslation();
  const history = useHistoryOf(sampleRecordType, record.id);
  const rows: [string, string][] = [
    [t("samples.name"), record.name],
    [t("samples.created"), formatDateTime(record.created_at)],
    [t("samples.updatedLabel"), formatDateTime(record.updated_at)],
  ];
  return (
    <RecordPage
      header={{ title: record.name, meta: [{ label: t("samples.version"), value: String(record.version) }] }}
      tags={{ recordType: sampleRecordType, recordId: record.id }}
      tabs={[
        {
          value: "details",
          label: t("samples.details"),
          panel: (
            <Table variant="vertical" withTableBorder>
              <Table.Tbody>
                {rows.map(([label, value]) => (
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
          value: "sharing",
          label: t("samples.sharing"),
          panel: <SharingControl recordType={sampleRecordType} recordId={record.id} />,
        },
      ]}
      history={history}
    />
  );
}
