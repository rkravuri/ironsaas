import { useCallback, useMemo, useState } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import {
  Alert,
  Badge,
  Box,
  Button,
  Code,
  Group,
  NumberInput,
  Paper,
  Select,
  Stack,
  Table,
  Tabs,
  Text,
  TextInput,
  Title,
} from "@mantine/core";
import { IconRepeat } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import {
  fetchDeadLetterPage,
  fetchEvent,
  fetchEventPage,
  fetchEventRetention,
  fetchEventTypes,
  replayEvents,
  type DeadLetter,
  type DomainEvent,
  type EventDelivery,
  type ReplayResult,
} from "@/api/events";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ReasonDialog } from "@/components/panels";
import { ListPage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";
import {
  emptyEventFilters as emptyFilters,
  eventFilterQuery,
  replayEventsPermission,
  type EventFilters as Filters,
} from "./event-filters";

const newestFirst = [{ field: "position", direction: "desc" as const }];
const deadNewestFirst = [{ field: "dead_at", direction: "desc" as const }];

type Status = DomainEvent["status"];

/** Where an event stands, as a badge whose words say it, so it is never colour alone (ADR-0024). */
function StatusBadge({ status }: { status: Status }) {
  const { t } = useTranslation();
  const color =
    status === "dead" ? "danger" : status === "pending" ? "info" : status === "handled" ? "success" : undefined;
  return (
    <Badge variant={color ? "light" : "outline"} {...(color ? { color } : {})}>
      {t(`events.statuses.${status}`)}
    </Badge>
  );
}

/** What one replay asks: one event to one subscriber, or a range of positions to one subscriber. */
type Replay = { kind: "event"; event: DomainEvent; subscriber: string } | { kind: "range" };

/**
 * The domain events of the organisation (TF-110 to TF-112, ADR-0060): what the modules told each other about their
 * changes, newest first, searched by type, record, correlation id and day, and the dead letters a subscriber parked after
 * its last attempt. The event chosen shows its data and where it stands with each subscriber. A holder of
 * events.events.replay replays an event to one subscriber, or a range of positions, with a reason the audit trail keeps,
 * after a recent sign-in: a dead letter runs again, an event the subscriber handled stays handled. <c>?event=</c> opens
 * one event; <c>?tab=dead-letters</c> the dead letters.
 */
export function EventsPage() {
  const { t } = useTranslation();
  const session = useSession();
  const canReplay = session.permissions.includes(replayEventsPermission);
  const [parameters] = useSearchParams();
  const [tab, setTab] = useState<"events" | "dead">(parameters.get("tab") === "dead-letters" ? "dead" : "events");
  const [filters, setFilters] = useState<Filters>(emptyFilters);
  const [applied, setApplied] = useState<Filters>(emptyFilters);
  const [refreshKey, setRefreshKey] = useState(0);
  const [chosen, setChosen] = useState<string | undefined>(parameters.get("event") ?? undefined);
  const [replay, setReplay] = useState<Replay | undefined>();
  const [message, setMessage] = useState<{ ok: boolean; text: string } | undefined>();
  const types = useQuery({ queryKey: ["event-types"], queryFn: ({ signal }) => fetchEventTypes(signal) });
  const retention = useQuery({ queryKey: ["event-retention"], queryFn: ({ signal }) => fetchEventRetention(signal) });
  const selected = useQuery({
    queryKey: ["event", chosen, refreshKey],
    queryFn: ({ signal }) => fetchEvent(chosen ?? "", signal),
    enabled: chosen != null,
    retry: false,
  });
  const shown = selected.data;

  const extra = useMemo(() => eventFilterQuery(applied), [applied]);
  const fetchPage = useCallback(
    (query: string, signal?: AbortSignal) =>
      fetchEventPage([query, ...extra].filter((p) => p !== "").join("&"), signal),
    [extra],
  );
  const subscribers = useMemo(
    () => [...new Set((types.data ?? []).flatMap((type) => type.subscribers.map((s) => s.name)))].sort(),
    [types.data],
  );

  const columns: DataGridColumn<DomainEvent>[] = [
    {
      field: "occurred_at",
      headerKey: "events.columns.occurred",
      width: 235,
      sortable: true,
      format: (e) => formatDateTime(e.occurred_at),
    },
    {
      field: "type",
      headerKey: "events.columns.type",
      flex: 1.2,
      minWidth: 260,
      sortable: true,
      format: (e) => t("events.typeVersion", { type: e.type, version: e.version }),
    },
    {
      field: "record_id",
      headerKey: "events.columns.record",
      flex: 1,
      minWidth: 260,
      format: (e) => `${e.record_type} ${e.record_id}`,
    },
    {
      field: "status",
      headerKey: "events.columns.status",
      width: 160,
      format: (e) => t(`events.statuses.${e.status}`),
    },
    {
      field: "position",
      headerKey: "events.columns.position",
      width: 120,
      sortable: true,
      format: (e) => `${e.position}`,
    },
  ];

  const deadColumns: DataGridColumn<DeadLetter>[] = [
    {
      field: "dead_at",
      headerKey: "events.columns.parked",
      width: 235,
      sortable: true,
      format: (d) => (d.dead_at ? formatDateTime(d.dead_at) : ""),
    },
    {
      field: "subscriber",
      headerKey: "events.columns.subscriber",
      flex: 1,
      minWidth: 240,
      sortable: true,
      format: (d) => d.subscriber,
    },
    {
      field: "type",
      headerKey: "events.columns.type",
      flex: 1,
      minWidth: 240,
      format: (d) => t("events.typeVersion", { type: d.type, version: d.version }),
    },
    { field: "attempts", headerKey: "events.columns.attempts", width: 120, format: (d) => `${d.attempts}` },
    {
      field: "last_error",
      headerKey: "events.columns.error",
      flex: 1.4,
      minWidth: 260,
      format: (d) => d.last_error ?? "",
    },
  ];

  const set = (key: keyof Filters) => (value: string) => setFilters((current) => ({ ...current, [key]: value }));
  const apply = () => {
    setApplied(filters);
    setRefreshKey((key) => key + 1);
  };

  const replayed = (result: ReplayResult) => {
    setReplay(undefined);
    setMessage({
      ok: true,
      text: t("events.replay.done", {
        count: result.events,
        subscriber: result.subscriber,
        requeued: result.requeued,
        added: result.added,
        handled: result.already_handled,
      }),
    });
    setRefreshKey((key) => key + 1);
  };

  return (
    <>
      <ListPage
        title={t("events.title")}
        description={t("events.description")}
        actions={
          canReplay ? (
            <Button
              variant="default"
              leftSection={<IconRepeat size={16} aria-hidden="true" />}
              onClick={() => setReplay({ kind: "range" })}
            >
              {t("events.replay.range")}
            </Button>
          ) : undefined
        }
        filters={
          tab === "events" ? (
            <Group gap="sm" align="flex-end" wrap="wrap" w="100%" data-event-filters>
              <Select
                label={t("events.filters.type")}
                value={filters.type}
                onChange={(value) => set("type")(value ?? "")}
                data={[
                  { value: "", label: t("events.filters.anyType") },
                  ...[...new Set((types.data ?? []).map((type) => type.name))].map((name) => ({
                    value: name,
                    label: name,
                  })),
                ]}
                allowDeselect={false}
                searchable
                w={300}
              />
              <TextInput
                label={t("events.filters.recordId")}
                value={filters.recordId}
                onChange={(e) => set("recordId")(e.currentTarget.value)}
                w={300}
              />
              <TextInput
                label={t("events.filters.correlationId")}
                value={filters.correlationId}
                onChange={(e) => set("correlationId")(e.currentTarget.value)}
                w={260}
              />
              <TextInput
                type="date"
                label={t("events.filters.from")}
                value={filters.from}
                onChange={(e) => set("from")(e.currentTarget.value)}
                w={170}
              />
              <TextInput
                type="date"
                label={t("events.filters.to")}
                value={filters.to}
                onChange={(e) => set("to")(e.currentTarget.value)}
                w={170}
              />
              <Button variant="default" onClick={apply}>
                {t("events.filters.apply")}
              </Button>
            </Group>
          ) : undefined
        }
      >
        <Stack gap="md">
          {retention.data && (
            <Text size="sm" c="dimmed" data-event-retention>
              {retention.data.archived_through
                ? t("events.retention.archived", {
                    count: retention.data.days,
                    through: formatDateTime(retention.data.archived_through),
                  })
                : t("events.retention.none", { count: retention.data.days })}
            </Text>
          )}
          {message && (
            <Alert
              color={message.ok ? "success" : "danger"}
              role={message.ok ? "status" : "alert"}
              withCloseButton
              closeButtonLabel={t("dialog.close")}
              onClose={() => setMessage(undefined)}
            >
              {message.text}
            </Alert>
          )}
          <Tabs value={tab} onChange={(next) => setTab(next === "dead" ? "dead" : "events")} keepMounted={false}>
            <Tabs.List>
              <Tabs.Tab value="events">{t("events.tabs.events")}</Tabs.Tab>
              <Tabs.Tab value="dead">{t("events.tabs.dead")}</Tabs.Tab>
            </Tabs.List>
            <Tabs.Panel value="events" pt="md">
              {/* A grid inside a tab has no height to fill, so it has its own, as on the audit page. */}
              <Box h={420}>
                <DataGrid<DomainEvent>
                  columns={columns}
                  fetchPage={fetchPage}
                  defaultSort={newestFirst}
                  label={t("events.gridLabel")}
                  refreshKey={refreshKey}
                  onSelectionChange={(row) => row && setChosen(row.id)}
                />
              </Box>
            </Tabs.Panel>
            <Tabs.Panel value="dead" pt="md">
              <Box h={420}>
                <DataGrid<DeadLetter>
                  columns={deadColumns}
                  fetchPage={fetchDeadLetterPage}
                  defaultSort={deadNewestFirst}
                  label={t("events.deadGridLabel")}
                  refreshKey={refreshKey}
                  onSelectionChange={(row) => row && setChosen(row.event_id)}
                />
              </Box>
            </Tabs.Panel>
          </Tabs>
          {shown ? (
            <EventDetail
              event={shown}
              canReplay={canReplay}
              onReplay={(subscriber) => setReplay({ kind: "event", event: shown, subscriber })}
            />
          ) : selected.error ? (
            <Alert color="danger" role="alert">
              {selected.error instanceof ApiError ? selected.error.message : t("errors.unexpected")}
            </Alert>
          ) : (
            <Text size="sm" c="dimmed">
              {t("events.choose")}
            </Text>
          )}
        </Stack>
      </ListPage>
      {replay?.kind === "event" && (
        <ReasonDialog
          opened
          title={t("events.replay.eventTitle")}
          message={t("events.replay.eventMessage", { type: replay.event.type, subscriber: replay.subscriber })}
          reasonLabel={t("events.replay.reason")}
          reasonHint={t("events.replay.reasonHint")}
          confirmLabel={t("events.replay.confirm")}
          onSubmit={async (reason) =>
            replayed(await replayEvents({ subscriber: replay.subscriber, event_ids: [replay.event.id], reason }))
          }
          onClose={() => setReplay(undefined)}
        />
      )}
      {replay?.kind === "range" && (
        <RangeDialog subscribers={subscribers} onReplayed={replayed} onClose={() => setReplay(undefined)} />
      )}
    </>
  );
}

/** A range of positions replayed to one subscriber: the events it reads between them, at most a thousand. */
function RangeDialog({
  subscribers,
  onReplayed,
  onClose,
}: {
  subscribers: string[];
  onReplayed: (result: ReplayResult) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const [subscriber, setSubscriber] = useState<string>(subscribers[0] ?? "");
  const [from, setFrom] = useState<number | "">("");
  const [to, setTo] = useState<number | "">("");
  return (
    <ReasonDialog
      opened
      title={t("events.replay.rangeTitle")}
      message={t("events.replay.rangeMessage")}
      reasonLabel={t("events.replay.reason")}
      reasonHint={t("events.replay.reasonHint")}
      confirmLabel={t("events.replay.confirm")}
      extra={
        <>
          <Select
            label={t("events.replay.subscriber")}
            value={subscriber}
            onChange={(value) => setSubscriber(value ?? "")}
            data={subscribers}
            allowDeselect={false}
            required
          />
          <Group grow>
            <NumberInput
              label={t("events.replay.fromPosition")}
              value={from}
              onChange={(value) => setFrom(typeof value === "number" ? value : "")}
              min={1}
              allowDecimal={false}
              required
            />
            <NumberInput
              label={t("events.replay.toPosition")}
              value={to}
              onChange={(value) => setTo(typeof value === "number" ? value : "")}
              min={1}
              allowDecimal={false}
              required
            />
          </Group>
        </>
      }
      onSubmit={async (reason) =>
        onReplayed(
          await replayEvents({
            subscriber,
            ...(from === "" ? {} : { from_position: from }),
            ...(to === "" ? {} : { to_position: to }),
            reason,
          }),
        )
      }
      onClose={onClose}
    />
  );
}

/** One event in full: what it is about, who acted, its data, and where it stands with each subscriber. */
function EventDetail({
  event,
  canReplay,
  onReplay,
}: {
  event: DomainEvent;
  canReplay: boolean;
  onReplay: (subscriber: string) => void;
}) {
  const { t } = useTranslation();
  const facts: [string, string][] = [
    [t("events.detail.record"), `${event.record_type} ${event.record_id}`],
    [t("events.detail.occurred"), formatDateTime(event.occurred_at)],
    [t("events.detail.actor"), event.actor_id],
    ...(event.on_behalf_of_id ? ([[t("events.detail.onBehalfOf"), event.on_behalf_of_id]] as [string, string][]) : []),
    [t("events.detail.correlation"), event.correlation_id ?? t("events.detail.none")],
    [t("events.detail.position"), `${event.position}`],
  ];
  return (
    <Paper component="section" withBorder p="md" data-event-detail aria-labelledby="event-detail-title">
      <Stack gap="sm">
        <Group gap="sm" align="center" wrap="wrap">
          <Title order={3} id="event-detail-title" size="h4">
            {t("events.typeVersion", { type: event.type, version: event.version })}
          </Title>
          <StatusBadge status={event.status} />
        </Group>
        <Table variant="vertical" withTableBorder maw={760}>
          <Table.Tbody>
            {facts.map(([label, value]) => (
              <Table.Tr key={label}>
                <Table.Th w={200}>{label}</Table.Th>
                <Table.Td>{value}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        <Title order={4} size="h5">
          {t("events.detail.data")}
        </Title>
        <Code block>{JSON.stringify(event.data, null, 2)}</Code>
        <Title order={4} size="h5">
          {t("events.detail.subscribers")}
        </Title>
        {event.deliveries.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t("events.detail.noSubscriber")}
          </Text>
        ) : (
          <Table withTableBorder striped data-event-deliveries>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("events.columns.subscriber")}</Table.Th>
                {/* Wide enough for the badge's words, which say the status so it is never colour alone. */}
                <Table.Th w={190}>{t("events.columns.status")}</Table.Th>
                <Table.Th>{t("events.columns.attempts")}</Table.Th>
                <Table.Th>{t("events.columns.when")}</Table.Th>
                <Table.Th>{t("events.columns.error")}</Table.Th>
                {canReplay && <Table.Th>{t("events.columns.action")}</Table.Th>}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {event.deliveries.map((delivery) => (
                <Table.Tr key={delivery.subscriber}>
                  <Table.Td>{delivery.subscriber}</Table.Td>
                  <Table.Td>
                    <StatusBadge status={delivery.status} />
                    {delivery.replays > 0 && (
                      <Text size="xs" c="dimmed">
                        {t("events.detail.replays", { count: delivery.replays })}
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td>{delivery.attempts}</Table.Td>
                  <Table.Td>{deliveryWhen(delivery, t)}</Table.Td>
                  <Table.Td>{delivery.last_error ?? ""}</Table.Td>
                  {canReplay && (
                    <Table.Td>
                      {/* A held delivery's subscriber is not in this unit: it resumes when its suite is added again. */}
                      {delivery.status !== "held" && (
                        <Button
                          size="compact-sm"
                          variant="default"
                          aria-label={t("events.replay.toSubscriber", { subscriber: delivery.subscriber })}
                          onClick={() => onReplay(delivery.subscriber)}
                        >
                          {t("events.replay.open")}
                        </Button>
                      )}
                    </Table.Td>
                  )}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </Stack>
    </Paper>
  );
}

/** When a delivery happened, since when it is held, or when it is tried next. */
function deliveryWhen(delivery: EventDelivery, t: TFunction): string {
  if (delivery.status === "held" && delivery.held_at) {
    return t("events.detail.heldAt", { at: formatDateTime(delivery.held_at) });
  }
  if (delivery.status === "handled" && delivery.handled_at) {
    return t("events.detail.handledAt", { at: formatDateTime(delivery.handled_at) });
  }
  if (delivery.status === "dead" && delivery.dead_at) {
    return t("events.detail.deadAt", { at: formatDateTime(delivery.dead_at) });
  }
  return t("events.detail.nextAt", { at: formatDateTime(delivery.next_attempt_at) });
}
