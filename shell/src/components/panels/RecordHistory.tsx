import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Alert, Text } from "@mantine/core";
import { ApiError } from "@/api/client";
import { auditQueryKey, fetchRecordHistory } from "@/api/audit";
import { HistoryPanel } from "./HistoryPanel";
import { LoadingState } from "./LoadingState";

/**
 * A record's audit events, newest first (TF-064 AC1, ADR-0050): every event that names the record, read from the audit
 * trail, on the History tab of the record template. Who did it, what, why, and when, in the person's time zone.
 */
export function RecordHistory({ recordType, recordId }: { recordType: string; recordId: string }) {
  const { t } = useTranslation();
  const history = useQuery({
    queryKey: [...auditQueryKey, "record", recordType, recordId],
    queryFn: ({ signal }) => fetchRecordHistory(recordType, recordId, signal),
  });
  if (history.isPending) {
    return <LoadingState height={160} />;
  }

  if (history.isError) {
    return (
      <Alert color="danger" role="alert">
        {history.error instanceof ApiError ? history.error.message : t("errors.unexpected")}
        {history.error instanceof ApiError && history.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: history.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }

  const events = history.data.data.map((event) => {
    const summary = [event.target, event.reason].filter((part): part is string => !!part).join(": ");
    return {
      id: event.id,
      at: event.created_at,
      actor: event.impersonator_name
        ? t("history.impersonated", {
            actor: event.actor_name ?? event.actor_id,
            impersonator: event.impersonator_name,
          })
        : (event.actor_name ?? event.actor_id),
      action: event.event_type,
      ...(summary !== "" ? { summary } : {}),
    };
  });
  return <HistoryPanel events={events} />;
}
