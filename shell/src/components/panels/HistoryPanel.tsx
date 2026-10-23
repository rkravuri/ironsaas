import { useTranslation } from "react-i18next";
import { Stack, Text, Timeline, Title } from "@mantine/core";
import { EmptyState } from "./EmptyState";
import { formatDateTime } from "@/i18n/dates";

/** One audit event of a record (ADR-0013), as the page reads it from the audit API. */
export interface HistoryEvent {
  id: string;
  at: string;
  actor: string;
  action: string;
  summary?: string;
}

/** What happened to a record, newest first, from its audit events (TF-171). */
export function HistoryPanel({ events }: { events: HistoryEvent[] }) {
  const { t } = useTranslation();
  return (
    <Stack gap="sm" data-panel="history">
      <Title order={4}>{t("history.title")}</Title>
      {events.length === 0 ? (
        <EmptyState drawing="nothingYet" title={t("history.empty")} size="sm" />
      ) : (
        <Timeline active={events.length} bulletSize={16} lineWidth={2}>
          {events.map((event) => (
            <Timeline.Item key={event.id} title={event.action}>
              {event.summary !== undefined && <Text size="sm">{event.summary}</Text>}
              <Text size="xs" c="dimmed">
                {event.actor}, {formatDateTime(event.at)}
              </Text>
            </Timeline.Item>
          ))}
        </Timeline>
      )}
    </Stack>
  );
}
