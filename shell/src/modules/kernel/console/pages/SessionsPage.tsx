import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Box, Button, Group, SimpleGrid, Stack, Table, Text, TextInput, Title } from "@mantine/core";
import { IconLogout, IconShieldOff } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import {
  endSession,
  fetchSessions,
  fetchSessionSummary,
  revokeTenant,
  sessionsQueryKey,
  type SessionRecord,
} from "@/api/identity";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { LoadingState, ReasonDialog } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { PlaceSource } from "@/modules/kernel/identity/pages/PlaceSource";
import { deviceOf, placeOf } from "@/modules/kernel/identity/pages/session-words";

const summaryDays = 14;
const liveSort = [{ field: "last_activity_at", direction: "desc" as const }];

/**
 * The sessions console (TF-022, TF-029, ADR-0049), for the holders of identity.sessions.manage: how many people are signed
 * in now and the summary of the last days; everyone's live sessions, any of which ends with a reason the person and the
 * audit trail keep; and, after an incident, every session and token of the tenant revoked at once, with a reason and the
 * tenant's identifier typed. Both changes ask the person to confirm it is them first (TF-248).
 */
export function SessionsPage() {
  const { t, i18n } = useTranslation();
  const session = useSession();
  const queryClient = useQueryClient();
  const [selected, setSelected] = useState<SessionRecord | undefined>();
  const [ending, setEnding] = useState<SessionRecord | undefined>();
  const [revoking, setRevoking] = useState(false);
  const [confirmation, setConfirmation] = useState("");
  const [done, setDone] = useState<string | undefined>();
  const [refreshKey, setRefreshKey] = useState(0);
  const summary = useQuery({
    queryKey: [...sessionsQueryKey, "summary", summaryDays],
    queryFn: ({ signal }) => fetchSessionSummary(summaryDays, signal),
  });
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchSessions(query, signal), []);
  const tenant = window.location.hostname.split(".")[0] ?? "";

  const day = (iso: string) =>
    new Date(`${iso}T00:00:00Z`).toLocaleDateString(i18n.language, { dateStyle: "medium", timeZone: "UTC" });
  const when = (iso: string) =>
    new Date(iso).toLocaleString(i18n.language, { dateStyle: "medium", timeStyle: "short" });

  const columns: DataGridColumn<SessionRecord>[] = [
    { field: "display_name", headerKey: "sessions.columns.person", flex: 2, sortable: true, filter: "text" },
    { field: "login_id", headerKey: "sessions.columns.login", flex: 2, hidden: true, filter: "text" },
    { field: "browser", headerKey: "sessions.columns.device", flex: 2, format: (row) => deviceOf(row, t) },
    {
      field: "country",
      headerKey: "sessions.columns.place",
      flex: 2,
      filter: "text",
      format: (row) => placeOf(row, t),
    },
    {
      field: "started_at",
      headerKey: "sessions.columns.started",
      width: 170,
      sortable: true,
      format: (row) => when(row.started_at),
    },
    {
      field: "last_activity_at",
      headerKey: "sessions.columns.lastActivity",
      width: 170,
      sortable: true,
      format: (row) => when(row.last_activity_at),
    },
    {
      field: "method",
      headerKey: "sessions.columns.method",
      width: 150,
      filter: "text",
      format: (row) =>
        row.impersonator_name
          ? t("sessions.methods.impersonationBy", { name: row.impersonator_name })
          : t(`sessions.methods.${row.method}`, { defaultValue: row.method }),
    },
  ];

  const ended = async (reason: string) => {
    if (!ending) {
      return;
    }
    await endSession(ending.id, reason);
    setDone(t("sessions.console.ended", { name: ending.display_name, device: deviceOf(ending, t) }));
    setEnding(undefined);
    setSelected(undefined);
    setRefreshKey((key) => key + 1);
    await queryClient.invalidateQueries({ queryKey: sessionsQueryKey });
  };

  const revoked = async (reason: string) => {
    await revokeTenant(reason, confirmation);
    setRevoking(false);
    // The asker's own session ended with everyone's: the next call finds it so and the login page says why.
    await session.refresh();
  };

  return (
    <>
      <ConsolePage
        title={t("sessions.console.title")}
        description={t("sessions.console.description")}
        sections={[
          {
            id: "summary",
            title: t("sessions.summary.title"),
            description: t("sessions.summary.description", { days: summaryDays }),
            children: summary.isPending ? (
              <LoadingState height={120} />
            ) : summary.isError ? (
              <Alert color="danger" role="alert">
                {summary.error instanceof ApiError ? summary.error.message : t("errors.unexpected")}
              </Alert>
            ) : (
              <Stack gap="md">
                <SimpleGrid cols={{ base: 1, sm: 3 }}>
                  <Stat label={t("sessions.summary.now")} value={summary.data.signed_in_now} />
                  <Stat label={t("sessions.summary.startedToday")} value={summary.data.days.at(-1)?.started ?? 0} />
                  <Stat label={t("sessions.summary.peakToday")} value={summary.data.days.at(-1)?.peak_people ?? 0} />
                </SimpleGrid>
                <Table withTableBorder striped data-session-summary>
                  <Table.Caption>{t("sessions.summary.caption", { days: summaryDays })}</Table.Caption>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>{t("sessions.summary.columns.day")}</Table.Th>
                      <Table.Th ta="right">{t("sessions.summary.columns.started")}</Table.Th>
                      <Table.Th ta="right">{t("sessions.summary.columns.peak")}</Table.Th>
                      <Table.Th ta="right">{t("sessions.summary.columns.ended")}</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {[...summary.data.days].reverse().map((row) => (
                      <Table.Tr key={row.date}>
                        <Table.Td>{day(row.date)}</Table.Td>
                        <Table.Td ta="right">{row.started}</Table.Td>
                        <Table.Td ta="right">{row.peak_people}</Table.Td>
                        <Table.Td ta="right">{row.ended_by_administrator}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Stack>
            ),
          },
          {
            id: "live",
            title: t("sessions.console.liveTitle"),
            description: t("sessions.console.liveDescription"),
            children: (
              <Stack gap="sm">
                <Group justify="flex-end">
                  <Button
                    variant="default"
                    leftSection={<IconLogout size={16} aria-hidden="true" />}
                    disabled={!selected || selected.current}
                    onClick={() => selected && setEnding(selected)}
                  >
                    {t("sessions.console.endSelected")}
                  </Button>
                </Group>
                {done && (
                  <Alert
                    color="success"
                    role="status"
                    withCloseButton
                    closeButtonLabel={t("dialog.close")}
                    onClose={() => setDone(undefined)}
                  >
                    {done}
                  </Alert>
                )}
                <Box h={460}>
                  <DataGrid<SessionRecord>
                    columns={columns}
                    fetchPage={fetchPage}
                    defaultSort={liveSort}
                    label={t("sessions.console.gridLabel")}
                    refreshKey={refreshKey}
                    onSelectionChange={setSelected}
                  />
                </Box>
                <PlaceSource />
              </Stack>
            ),
          },
          {
            id: "revoke",
            title: t("sessions.revokeTenant.title"),
            description: t("sessions.revokeTenant.description"),
            children: (
              <Group justify="space-between" align="center" wrap="wrap">
                <Text size="sm" maw={560}>
                  {t("sessions.revokeTenant.when")}
                </Text>
                <Button
                  color="danger"
                  leftSection={<IconShieldOff size={16} aria-hidden="true" />}
                  onClick={() => setRevoking(true)}
                >
                  {t("sessions.revokeTenant.open")}
                </Button>
              </Group>
            ),
          },
        ]}
      />
      {ending && (
        <ReasonDialog
          opened
          title={t("sessions.console.endTitle", { name: ending.display_name })}
          message={t("sessions.console.endMessage", {
            name: ending.display_name,
            device: deviceOf(ending, t),
            place: placeOf(ending, t),
          })}
          reasonLabel={t("reason.label")}
          reasonHint={t("sessions.console.reasonHint", { name: ending.display_name })}
          confirmLabel={t("sessions.console.endConfirm")}
          onSubmit={ended}
          onClose={() => setEnding(undefined)}
        />
      )}
      {revoking && (
        <ReasonDialog
          opened
          title={t("sessions.revokeTenant.dialogTitle")}
          message={t("sessions.revokeTenant.message")}
          reasonLabel={t("reason.label")}
          reasonHint={t("sessions.revokeTenant.reasonHint")}
          confirmLabel={t("sessions.revokeTenant.confirm")}
          extra={
            <TextInput
              label={t("sessions.revokeTenant.typeLabel", { tenant })}
              description={t("sessions.revokeTenant.typeHint")}
              required
              value={confirmation}
              onChange={(event) => setConfirmation(event.currentTarget.value)}
              autoComplete="off"
            />
          }
          onSubmit={revoked}
          onClose={() => {
            setRevoking(false);
            setConfirmation("");
          }}
        />
      )}
    </>
  );
}

function Stat({ label, value }: { label: string; value: number }) {
  return (
    <Stack gap={0}>
      <Text size="sm" c="dimmed">
        {label}
      </Text>
      <Title order={4} component="p">
        {value}
      </Title>
    </Stack>
  );
}
