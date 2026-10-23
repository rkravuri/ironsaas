import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Badge, Box, Button, Group, Select, Stack, Table, Tabs, Text, TextInput } from "@mantine/core";
import { IconArchive, IconFileExport } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { exportAuditEvents, fetchAuditEvents, searchArchive, type ArchivedEvents, type AuditEvent } from "@/api/audit";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { EmptyState } from "@/components/panels";
import { ListPage } from "@/components/templates";
import { formatDateTime, startOfDay } from "@/i18n/dates";
import { PersonPicker } from "@/modules/kernel/console/pages/PersonPicker";
import { emptyFilters as empty, endOfDay, filterQuery, type Filters } from "./audit-filters";
import { actorWords, outcomeWords, severityWords } from "./audit-words";

const newestFirst = [{ field: "created_at", direction: "desc" as const }];
const sources = ["ui", "api", "job"] as const;

/**
 * The audit trail (TF-064, ADR-0050): a search by time, who acted, who signed in as them, record, type and source over
 * the events in the database, newest first, any of whose rows the grid's own filters narrow further; its export to CSV,
 * the search as applied, with the filters in the file's first lines, handed over as a download; and, on a tab of its own,
 * a search of the archive by date range, for the events older than the online period. Every time is in the person's
 * zone, with its name.
 */
export function AuditPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useState<"search" | "archive">("search");
  const [filters, setFilters] = useState<Filters>(empty);
  const [applied, setApplied] = useState<Filters>(empty);
  const [refreshKey, setRefreshKey] = useState(0);
  const [exporting, setExporting] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | undefined>();
  const [archive, setArchive] = useState<ArchivedEvents | undefined>();
  const [searching, setSearching] = useState(false);

  const extra = useMemo(() => filterQuery(applied), [applied]);
  const fetchPage = useCallback(
    (query: string, signal?: AbortSignal) =>
      fetchAuditEvents([query, ...extra].filter((p) => p !== "").join("&"), signal),
    [extra],
  );

  const columns: DataGridColumn<AuditEvent>[] = [
    {
      field: "created_at",
      headerKey: "audit.columns.at",
      // The zone's name reads in full, "GMT+5:30" as much as "UTC" (TF-065 AC2).
      width: 235,
      sortable: true,
      format: (e) => formatDateTime(e.created_at),
    },
    { field: "event_type", headerKey: "audit.columns.event", flex: 1, minWidth: 200, sortable: true, filter: "text" },
    // Who reads in full at 1280 pixels, an operator signed in as someone too.
    { field: "actor_id", headerKey: "audit.columns.actor", flex: 1.3, minWidth: 250, format: (e) => actorWords(e, t) },
    { field: "target", headerKey: "audit.columns.target", flex: 1, minWidth: 150 },
    { field: "record_type", headerKey: "audit.columns.recordType", width: 150, hidden: true, filter: "text" },
    {
      field: "outcome",
      headerKey: "audit.columns.outcome",
      width: 115,
      filter: "text",
      format: (e) => outcomeWords(e.outcome, t),
    },
    {
      // The filters above choose a source; the column is there to show.
      field: "source",
      headerKey: "audit.columns.source",
      width: 110,
      hidden: true,
      filter: "text",
      format: (e) => t(`audit.sources.${e.source}`, { defaultValue: e.source }),
    },
    {
      field: "severity",
      headerKey: "audit.columns.severity",
      width: 120,
      filter: "text",
      format: (e) => severityWords(e.severity, t),
    },
    {
      field: "chain_position",
      headerKey: "audit.columns.position",
      width: 130,
      sortable: true,
      format: (e) => (e.chain_position == null ? t("audit.unsealed") : String(e.chain_position)),
    },
  ];

  const apply = () => {
    setApplied(filters);
    setRefreshKey((key) => key + 1);
  };

  const exportCsv = async () => {
    setExporting(true);
    setMessage(undefined);
    try {
      const done = await exportAuditEvents([...extra, "sort=-created_at"].join("&"));
      const link = document.createElement("a");
      link.href = done.download_url;
      link.download = "";
      document.body.append(link);
      link.click();
      link.remove();
      setMessage({ ok: true, text: t("audit.export.done", { count: done.rows }) });
    } catch (error) {
      setMessage({ ok: false, text: error instanceof ApiError ? error.message : t("errors.unexpected") });
    } finally {
      setExporting(false);
    }
  };

  const searchTheArchive = async () => {
    setSearching(true);
    setMessage(undefined);
    try {
      const to = endOfDay(filters.to);
      setArchive(
        await searchArchive({
          from: startOfDay(filters.from).toISOString(),
          to: new Date(to.getTime() - 1).toISOString(),
          event_type: filters.eventType.trim(),
          actor_id: filters.actorId,
          impersonator_id: filters.impersonatorId,
          record_type: filters.recordType.trim(),
          record_id: filters.recordId.trim(),
          source: filters.source,
        }),
      );
    } catch (error) {
      setMessage({ ok: false, text: error instanceof ApiError ? error.message : t("errors.unexpected") });
    } finally {
      setSearching(false);
    }
  };

  const set = (key: keyof Filters) => (value: string) => setFilters((current) => ({ ...current, [key]: value }));

  return (
    <ListPage
      title={t("audit.title")}
      description={t("audit.description")}
      actions={
        tab === "search" ? (
          <Button
            variant="default"
            leftSection={<IconFileExport size={16} aria-hidden="true" />}
            loading={exporting}
            onClick={() => void exportCsv()}
          >
            {t("audit.export.open")}
          </Button>
        ) : undefined
      }
      density={tab === "search"}
      filters={
        <Group gap="sm" align="flex-end" wrap="wrap" w="100%" data-audit-filters>
          <TextInput
            label={t("audit.filters.eventType")}
            value={filters.eventType}
            onChange={(e) => set("eventType")(e.currentTarget.value)}
            w={220}
          />
          <PersonPicker
            label={t("audit.filters.actor")}
            placeholder={t("audit.filters.actorPlaceholder")}
            value={filters.actorId === "" ? null : filters.actorId}
            onChange={(value) => set("actorId")(value ?? "")}
            clearable
            w={240}
          />
          <PersonPicker
            label={t("audit.filters.impersonator")}
            placeholder={t("audit.filters.impersonatorPlaceholder")}
            value={filters.impersonatorId === "" ? null : filters.impersonatorId}
            onChange={(value) => set("impersonatorId")(value ?? "")}
            clearable
            w={240}
          />
          <TextInput
            label={t("audit.filters.recordType")}
            value={filters.recordType}
            onChange={(e) => set("recordType")(e.currentTarget.value)}
            w={170}
          />
          <TextInput
            label={t("audit.filters.recordId")}
            value={filters.recordId}
            onChange={(e) => set("recordId")(e.currentTarget.value)}
            w={300}
          />
          <Select
            label={t("audit.filters.source")}
            value={filters.source}
            onChange={(value) => set("source")(value ?? "")}
            data={[
              { value: "", label: t("audit.filters.anySource") },
              ...sources.map((s) => ({ value: s, label: t(`audit.sources.${s}`) })),
            ]}
            allowDeselect={false}
            w={150}
          />
          <TextInput
            type="date"
            label={t("audit.filters.from")}
            value={filters.from}
            onChange={(e) => set("from")(e.currentTarget.value)}
            w={170}
          />
          <TextInput
            type="date"
            label={t("audit.filters.to")}
            value={filters.to}
            onChange={(e) => set("to")(e.currentTarget.value)}
            w={170}
          />
          {tab === "search" ? (
            <Button variant="default" onClick={apply}>
              {t("audit.filters.apply")}
            </Button>
          ) : (
            <Button
              variant="default"
              leftSection={<IconArchive size={16} aria-hidden="true" />}
              disabled={filters.from === "" || filters.to === ""}
              loading={searching}
              onClick={() => void searchTheArchive()}
            >
              {t("audit.archive.search")}
            </Button>
          )}
        </Group>
      }
    >
      <Tabs value={tab} onChange={(next) => setTab(next === "archive" ? "archive" : "search")} keepMounted={false}>
        <Tabs.List>
          <Tabs.Tab value="search">{t("audit.tabs.search")}</Tabs.Tab>
          <Tabs.Tab value="archive">{t("audit.tabs.archive")}</Tabs.Tab>
        </Tabs.List>
        {message && (
          <Alert color={message.ok ? "success" : "danger"} role={message.ok ? "status" : "alert"} mt="md">
            {message.text}
          </Alert>
        )}
        <Tabs.Panel value="search" pt="md">
          {/* A grid inside a tab has no height to fill, so it has its own, as on the conflicts page. */}
          <Box h={480}>
            <DataGrid<AuditEvent>
              columns={columns}
              fetchPage={fetchPage}
              defaultSort={newestFirst}
              label={t("audit.gridLabel")}
              refreshKey={refreshKey}
            />
          </Box>
        </Tabs.Panel>
        <Tabs.Panel value="archive" pt="md">
          <Stack gap="sm">
            <Text size="sm" c="dimmed" maw={760}>
              {t("audit.archive.explain")}
            </Text>
            {archive === undefined ? null : archive.data.length === 0 ? (
              <EmptyState
                drawing="nothingYet"
                size="sm"
                title={t("audit.archive.none")}
                text={t("audit.archive.noneText", { count: archive.batches_read })}
              />
            ) : (
              <Table withTableBorder striped data-archive-results>
                <Table.Caption>
                  {archive.truncated
                    ? t("audit.archive.truncated", { count: archive.data.length })
                    : t("audit.archive.caption", { count: archive.data.length, batches: archive.batches_read })}
                </Table.Caption>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t("audit.columns.at")}</Table.Th>
                    <Table.Th>{t("audit.columns.event")}</Table.Th>
                    <Table.Th>{t("audit.columns.actor")}</Table.Th>
                    <Table.Th>{t("audit.columns.target")}</Table.Th>
                    <Table.Th>{t("audit.columns.position")}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {archive.data.map((event) => (
                    <Table.Tr key={event.id}>
                      <Table.Td>{formatDateTime(event.created_at)}</Table.Td>
                      <Table.Td>
                        {event.event_type}{" "}
                        <Badge variant="light" color="info" size="sm">
                          {t("audit.archive.badge")}
                        </Badge>
                      </Table.Td>
                      <Table.Td>{actorWords(event, t)}</Table.Td>
                      <Table.Td>{event.target ?? ""}</Table.Td>
                      <Table.Td>{event.chain_position ?? ""}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Stack>
        </Tabs.Panel>
      </Tabs>
    </ListPage>
  );
}
