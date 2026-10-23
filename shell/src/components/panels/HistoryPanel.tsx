import { useTranslation } from "react-i18next";
import { Stack, Table, Text, Timeline, Title } from "@mantine/core";
import { EmptyState } from "./EmptyState";
import { fieldWords } from "./field-words";
import { formatDateTime } from "@/i18n/dates";

/** One field of a save (TF-062): old and new as the history keeps them. */
export interface HistoryFieldChange {
  field: string;
  old: string | null;
  new: string | null;
  treatment: "plain" | "masked" | "secret";
}

/** One audit event of a record (ADR-0013), as the page reads it from the audit API. */
export interface HistoryEvent {
  id: string;
  at: string;
  actor: string;
  action: string;
  summary?: string;
  /** A save of the record: every field it changed, shown together as one group (TF-062 AC1). */
  changes?: HistoryFieldChange[];
}

/** What happened to a record, newest first, from its audit events (TF-171); a save's fields side by side, old and new. */
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
              {event.changes !== undefined && event.changes.length > 0 && <Changes changes={event.changes} />}
            </Timeline.Item>
          ))}
        </Timeline>
      )}
    </Stack>
  );
}

function Changes({ changes }: { changes: HistoryFieldChange[] }) {
  const { t } = useTranslation();
  const value = (change: HistoryFieldChange, text: string | null) =>
    text === null ? (
      <Text span size="sm" c="dimmed">
        {t("history.changes.none")}
      </Text>
    ) : (
      <Text span size="sm" {...(change.treatment === "masked" ? { ff: "monospace" } : {})}>
        {text}
      </Text>
    );
  return (
    <Table withTableBorder mt={6} verticalSpacing={4} data-changes>
      <Table.Caption>{t("history.changes.caption", { count: changes.length })}</Table.Caption>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>{t("history.changes.field")}</Table.Th>
          <Table.Th>{t("history.changes.old")}</Table.Th>
          <Table.Th>{t("history.changes.new")}</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {changes.map((change) => (
          <Table.Tr key={change.field} data-field={change.field}>
            <Table.Td>
              <Text size="sm" fw={600}>
                {fieldWords(change.field)}
              </Text>
              {change.treatment === "masked" && (
                <Text size="xs" c="dimmed">
                  {t("history.changes.masked")}
                </Text>
              )}
            </Table.Td>
            {change.treatment === "secret" ? (
              <Table.Td colSpan={2}>
                <Text size="sm">{t("history.changes.secret")}</Text>
              </Table.Td>
            ) : (
              <>
                <Table.Td>{value(change, change.old)}</Table.Td>
                <Table.Td>{value(change, change.new)}</Table.Td>
              </>
            )}
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}
