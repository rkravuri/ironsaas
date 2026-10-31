import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Alert, Anchor, Badge, Box, Button, Group, Paper, Stack, Text, Title } from "@mantine/core";
import { replayEvents } from "@/api/events";
import { fetchAnyDeadLetterPage, rerunJobRun, type AnyDeadLetter } from "@/api/jobs";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ReasonDialog } from "@/components/panels";
import { ListPage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";
import { replayEventsPermission } from "./event-filters";
import { manageJobs } from "./job-words";

const newestFirst = [{ field: "dead_at", direction: "desc" as const }];

/**
 * Every dead letter of the organisation in one view (TF-123, ADR-0061): the runs of jobs that failed as often as their
 * job is retried, and the events a subscriber parked after its last attempt, newest first; the events only for a person
 * who may read them. The one chosen shows its error. A job's is run again by a holder of jobs.jobs.manage, an event's is
 * replayed to its subscriber by a holder of events.events.replay, each with a reason the audit trail keeps.
 */
export function DeadLettersPage() {
  const { t } = useTranslation();
  const session = useSession();
  const canRerun = session.permissions.includes(manageJobs);
  const canReplay = session.permissions.includes(replayEventsPermission);
  const [chosen, setChosen] = useState<AnyDeadLetter | undefined>();
  const [acting, setActing] = useState<AnyDeadLetter | undefined>();
  const [message, setMessage] = useState<string | undefined>();
  const [refreshKey, setRefreshKey] = useState(0);

  const columns: DataGridColumn<AnyDeadLetter>[] = [
    {
      field: "dead_at",
      headerKey: "deadLetters.columns.parked",
      width: 235,
      sortable: true,
      format: (d) => formatDateTime(d.dead_at),
    },
    {
      field: "kind",
      headerKey: "deadLetters.columns.kind",
      width: 150,
      format: (d) => t(`deadLetters.kinds.${d.kind}`),
    },
    { field: "name", headerKey: "deadLetters.columns.name", flex: 1, minWidth: 240, format: (d) => d.name },
    { field: "what", headerKey: "deadLetters.columns.what", flex: 1, minWidth: 240, format: (d) => d.what },
    { field: "attempts", headerKey: "deadLetters.columns.attempts", width: 120, format: (d) => `${d.attempts}` },
    { field: "error", headerKey: "deadLetters.columns.error", flex: 1.4, minWidth: 260, format: (d) => d.error ?? "" },
  ];

  const mayAct = (letter: AnyDeadLetter) => (letter.kind === "job" ? canRerun : canReplay);
  return (
    <>
      <ListPage title={t("deadLetters.title")} description={t("deadLetters.description")}>
        <Stack gap="md">
          {message && (
            <Alert
              color="success"
              role="status"
              withCloseButton
              closeButtonLabel={t("dialog.close")}
              onClose={() => setMessage(undefined)}
            >
              {message}
            </Alert>
          )}
          <Box h={420}>
            <DataGrid<AnyDeadLetter>
              columns={columns}
              fetchPage={fetchAnyDeadLetterPage}
              defaultSort={newestFirst}
              label={t("deadLetters.gridLabel")}
              refreshKey={refreshKey}
              onSelectionChange={setChosen}
            />
          </Box>
          {chosen ? (
            <Paper component="section" withBorder p="md" data-dead-letter aria-labelledby="dead-letter-title">
              <Stack gap="sm">
                <Group gap="sm" align="center" wrap="wrap">
                  <Title order={3} id="dead-letter-title" size="h4">
                    {chosen.name}
                  </Title>
                  <Badge variant="outline">{t(`deadLetters.kinds.${chosen.kind}`)}</Badge>
                </Group>
                <Text size="sm">
                  {t(`deadLetters.detail.${chosen.kind}`, {
                    what: chosen.what,
                    attempts: chosen.attempts,
                    at: formatDateTime(chosen.dead_at),
                  })}
                </Text>
                {chosen.error && (
                  <Text size="sm" fw={600}>
                    {chosen.error}
                  </Text>
                )}
                <Group gap="sm">
                  {mayAct(chosen) && (
                    <Button variant="default" onClick={() => setActing(chosen)}>
                      {t(`deadLetters.actions.${chosen.kind}.open`)}
                    </Button>
                  )}
                  <Anchor
                    component={Link}
                    to={
                      chosen.kind === "job"
                        ? `/console/jobs?job=${encodeURIComponent(chosen.name)}`
                        : `/console/events?event=${encodeURIComponent(chosen.id)}`
                    }
                    size="sm"
                  >
                    {t(`deadLetters.open.${chosen.kind}`)}
                  </Anchor>
                </Group>
              </Stack>
            </Paper>
          ) : (
            <Text size="sm" c="dimmed">
              {t("deadLetters.choose")}
            </Text>
          )}
        </Stack>
      </ListPage>
      {acting && (
        <ReasonDialog
          opened
          title={t(`deadLetters.actions.${acting.kind}.title`)}
          message={t(`deadLetters.actions.${acting.kind}.message`, { name: acting.name, what: acting.what })}
          reasonLabel={t("jobs.actions.reason")}
          reasonHint={t("jobs.actions.reasonHint")}
          confirmLabel={t(`deadLetters.actions.${acting.kind}.confirm`)}
          onSubmit={async (reason) => {
            if (acting.kind === "job") {
              await rerunJobRun(acting.id, reason);
            } else {
              await replayEvents({ subscriber: acting.name, event_ids: [acting.id], reason });
            }
            setMessage(t(`deadLetters.actions.${acting.kind}.done`, { name: acting.name }));
            setActing(undefined);
            setChosen(undefined);
            setRefreshKey((key) => key + 1);
          }}
          onClose={() => setActing(undefined)}
        />
      )}
    </>
  );
}
