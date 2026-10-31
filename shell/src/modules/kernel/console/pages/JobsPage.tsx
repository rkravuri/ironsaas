import { useCallback, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Badge, Box, Button, Code, Group, Stack, Table, Text, Title } from "@mantine/core";
import { IconPlayerPause, IconPlayerPlay, IconRepeat, IconRun } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import {
  actOnJob,
  fetchJob,
  fetchJobRun,
  fetchJobRunPage,
  fetchJobs,
  rerunJobRun,
  type Job,
  type JobAction,
  type JobRun,
} from "@/api/jobs";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { LoadingState, ReasonDialog } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";
import { actsOnRuns, durationWords, manageJobs, rerunnable, scheduleWords } from "./job-words";

const jobsKey = ["jobs"] as const;
const newestFirst = [{ field: "scheduled_for", direction: "desc" as const }];

/** Where a job stands, in words, so it is never colour alone (ADR-0024). */
function StateBadge({ state }: { state: Job["state"] }) {
  const { t } = useTranslation();
  const color = state === "paused" ? "warning" : state === "active" ? "success" : undefined;
  return (
    <Badge variant={color ? "light" : "outline"} {...(color ? { color } : {})}>
      {t(`jobs.states.${state}`)}
    </Badge>
  );
}

/** Where a run stands, in words. */
function RunBadge({ status }: { status: JobRun["status"] }) {
  const { t } = useTranslation();
  const color =
    status === "dead" ? "danger" : status === "succeeded" ? "success" : status === "running" ? "info" : undefined;
  return (
    <Badge variant={color ? "light" : "outline"} {...(color ? { color } : {})}>
      {t(`jobs.runStatuses.${status}`)}
    </Badge>
  );
}

/** What one action asks: on a job, or a dead run run again. */
type Acting = { kind: "job"; job: Job; action: JobAction } | { kind: "rerun"; run: JobRun };

/**
 * The organisation's jobs (TF-120 to TF-124, ADR-0061): every job its modules declare, with its schedule, priority class,
 * state, next and last runs, how long it usually takes, its backlog and its dead letters. The job chosen shows its last
 * failure with its error and log (TF-121 AC1) and its runs; the run chosen shows its log. A holder of jobs.jobs.manage
 * pauses, resumes or starts a job now, and runs a dead letter again, each with a reason the audit trail keeps, after a
 * recent sign-in (TF-121 AC2). <c>?job=</c> opens one job.
 */
export function JobsPage() {
  const { t } = useTranslation();
  const session = useSession();
  const queryClient = useQueryClient();
  const canManage = session.permissions.includes(manageJobs);
  const [parameters, setParameters] = useSearchParams();
  const chosen = parameters.get("job") ?? undefined;
  const [chosenRun, setChosenRun] = useState<string | undefined>();
  const [acting, setActing] = useState<Acting | undefined>();
  const [message, setMessage] = useState<string | undefined>();
  const [refreshKey, setRefreshKey] = useState(0);
  const jobs = useQuery({ queryKey: jobsKey, queryFn: ({ signal }) => fetchJobs(signal), refetchInterval: 30_000 });
  const job = useQuery({
    queryKey: ["job", chosen, refreshKey],
    queryFn: ({ signal }) => fetchJob(chosen ?? "", signal),
    enabled: chosen != null,
    retry: false,
  });
  const run = useQuery({
    queryKey: ["job-run", chosenRun, refreshKey],
    queryFn: ({ signal }) => fetchJobRun(chosenRun ?? "", signal),
    enabled: chosenRun != null,
    retry: false,
  });
  const fetchRuns = useCallback(
    (query: string, signal?: AbortSignal) => fetchJobRunPage(chosen ?? "", query, signal),
    [chosen],
  );

  const choose = (name: string) => {
    setChosenRun(undefined);
    setParameters((current) => {
      const next = new URLSearchParams(current);
      next.set("job", name);
      return next;
    });
  };

  const done = async (text: string) => {
    setActing(undefined);
    setMessage(text);
    setRefreshKey((key) => key + 1);
    await queryClient.invalidateQueries({ queryKey: jobsKey });
  };

  const runColumns: DataGridColumn<JobRun>[] = [
    {
      field: "scheduled_for",
      headerKey: "jobs.columns.scheduledFor",
      width: 235,
      sortable: true,
      format: (r) => formatDateTime(r.scheduled_for),
    },
    { field: "status", headerKey: "jobs.columns.status", width: 160, format: (r) => t(`jobs.runStatuses.${r.status}`) },
    {
      field: "attempts",
      headerKey: "jobs.columns.attempts",
      width: 120,
      sortable: true,
      format: (r) => `${r.attempts}`,
    },
    {
      field: "duration_ms",
      headerKey: "jobs.columns.duration",
      width: 140,
      sortable: true,
      format: (r) => durationWords(r.duration_ms, t),
    },
    { field: "key", headerKey: "jobs.columns.key", flex: 1, minWidth: 220, format: (r) => r.key },
    { field: "error", headerKey: "jobs.columns.error", flex: 1.4, minWidth: 260, format: (r) => r.error ?? "" },
  ];

  const shown = job.data;
  return (
    <>
      <ConsolePage
        title={t("jobs.title")}
        description={t("jobs.description")}
        sections={[
          {
            id: "jobs",
            title: t("jobs.sectionTitle"),
            description: t("jobs.sectionDescription"),
            children: (
              <Stack gap="sm">
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
                {jobs.isPending ? (
                  <LoadingState height={200} />
                ) : jobs.isError ? (
                  <ProblemAlert error={jobs.error} />
                ) : (
                  <Box style={{ overflowX: "auto" }}>
                    <Table withTableBorder striped data-jobs>
                      <Table.Caption>{t("jobs.caption")}</Table.Caption>
                      <Table.Thead>
                        <Table.Tr>
                          <Table.Th>{t("jobs.columns.job")}</Table.Th>
                          <Table.Th>{t("jobs.columns.schedule")}</Table.Th>
                          {/* Wide enough for the badge's words, which say the state so it is never colour alone. */}
                          <Table.Th w={170}>{t("jobs.columns.state")}</Table.Th>
                          <Table.Th>{t("jobs.columns.lastRun")}</Table.Th>
                          <Table.Th>{t("jobs.columns.backlog")}</Table.Th>
                          <Table.Th>{t("jobs.columns.action")}</Table.Th>
                        </Table.Tr>
                      </Table.Thead>
                      <Table.Tbody>
                        {jobs.data.map((item) => (
                          <Table.Tr key={item.name} data-job={item.name}>
                            <Table.Td>
                              <Code>{item.name}</Code>
                              <Text size="xs" c="dimmed" maw={360}>
                                {item.description}
                              </Text>
                              {item.suite && (
                                <Text size="xs" c="dimmed">
                                  {t("jobs.suite", { suite: item.suite })}
                                </Text>
                              )}
                            </Table.Td>
                            <Table.Td>
                              <Text size="sm">{scheduleWords(item.schedule, t)}</Text>
                              <Text size="xs" c="dimmed">
                                {t(`jobs.priorities.${item.priority}`)}
                              </Text>
                            </Table.Td>
                            <Table.Td>
                              <StateBadge state={item.state} />
                              <Text size="xs" c="dimmed" mt={2}>
                                {item.next_run_at
                                  ? t("jobs.nextRun", { at: formatDateTime(item.next_run_at) })
                                  : item.scope === "each_process"
                                    ? t("jobs.eachProcess")
                                    : t("jobs.noNextRun")}
                              </Text>
                            </Table.Td>
                            <Table.Td>
                              {item.last_run ? (
                                <>
                                  <RunBadge status={item.last_run.status} />
                                  <Text size="xs" c="dimmed" mt={2}>
                                    {t("jobs.lastRunAt", {
                                      at: formatDateTime(item.last_run.finished_at ?? item.last_run.scheduled_for),
                                      duration: durationWords(item.last_run.duration_ms, t),
                                    })}
                                  </Text>
                                </>
                              ) : (
                                <Text size="sm" c="dimmed">
                                  {t("jobs.notRunYet")}
                                </Text>
                              )}
                            </Table.Td>
                            <Table.Td>
                              <Text size="sm">{t("jobs.backlog", { queued: item.queued, running: item.running })}</Text>
                              {item.dead_letters > 0 && (
                                <Text size="xs" fw={600}>
                                  {t("jobs.deadLetters", { count: item.dead_letters })}
                                </Text>
                              )}
                            </Table.Td>
                            <Table.Td>
                              <Button
                                variant="subtle"
                                size="compact-sm"
                                aria-label={t("jobs.openNamed", { name: item.name })}
                                onClick={() => choose(item.name)}
                              >
                                {t("jobs.open")}
                              </Button>
                            </Table.Td>
                          </Table.Tr>
                        ))}
                      </Table.Tbody>
                    </Table>
                  </Box>
                )}
              </Stack>
            ),
          },
          {
            id: "job",
            title: shown ? shown.name : t("jobs.detailTitle"),
            description: shown ? shown.description : t("jobs.choose"),
            children: shown ? (
              <JobDetail
                job={shown}
                canManage={canManage}
                onAct={(action) => setActing({ kind: "job", job: shown, action })}
              >
                <Title order={4} size="h5">
                  {t("jobs.detail.runs")}
                </Title>
                {shown.scope === "each_process" ? (
                  <Text size="sm" c="dimmed">
                    {t("jobs.detail.eachProcessRuns")}
                  </Text>
                ) : (
                  <Box h={360}>
                    <DataGrid<JobRun>
                      key={shown.name}
                      columns={runColumns}
                      fetchPage={fetchRuns}
                      defaultSort={newestFirst}
                      label={t("jobs.runsGridLabel", { name: shown.name })}
                      refreshKey={refreshKey}
                      onSelectionChange={(row) => row && setChosenRun(row.id)}
                    />
                  </Box>
                )}
                {run.data && (
                  <RunDetail
                    run={run.data}
                    canManage={canManage}
                    onRerun={() => run.data && setActing({ kind: "rerun", run: run.data })}
                  />
                )}
                {run.error && <ProblemAlert error={run.error} />}
              </JobDetail>
            ) : job.error ? (
              <ProblemAlert error={job.error} />
            ) : (
              <Text size="sm" c="dimmed">
                {t("jobs.choose")}
              </Text>
            ),
          },
        ]}
      />
      {acting?.kind === "job" && (
        <ReasonDialog
          opened
          title={t(`jobs.actions.${acting.action}.title`, { name: acting.job.name })}
          message={t(`jobs.actions.${acting.action}.message`, { name: acting.job.name })}
          reasonLabel={t("jobs.actions.reason")}
          reasonHint={t("jobs.actions.reasonHint")}
          confirmLabel={t(`jobs.actions.${acting.action}.confirm`)}
          onSubmit={async (reason) => {
            await actOnJob(acting.job.name, acting.action, reason);
            await done(t(`jobs.actions.${acting.action}.done`, { name: acting.job.name }));
          }}
          onClose={() => setActing(undefined)}
        />
      )}
      {acting?.kind === "rerun" && (
        <ReasonDialog
          opened
          title={t("jobs.actions.rerun.title")}
          message={t("jobs.actions.rerun.message", { name: acting.run.job })}
          reasonLabel={t("jobs.actions.reason")}
          reasonHint={t("jobs.actions.reasonHint")}
          confirmLabel={t("jobs.actions.rerun.confirm")}
          onSubmit={async (reason) => {
            await rerunJobRun(acting.run.id, reason);
            await done(t("jobs.actions.rerun.done", { name: acting.run.job }));
          }}
          onClose={() => setActing(undefined)}
        />
      )}
    </>
  );
}

/** A problem from the API, with its correlation id, which the error views show (ADR-0012). */
function ProblemAlert({ error }: { error: unknown }) {
  const { t } = useTranslation();
  return (
    <Alert color="danger" role="alert">
      {error instanceof ApiError ? error.message : t("errors.unexpected")}
      {error instanceof ApiError && error.correlationId !== undefined && (
        <Text size="xs" c="dimmed" mt={4}>
          {t("errors.correlation", { id: error.correlationId })}
        </Text>
      )}
    </Alert>
  );
}

/** One job in full: how it runs, why it is paused, its last failure with its error and log, its actions and its runs. */
function JobDetail({
  job,
  canManage,
  onAct,
  children,
}: {
  job: Job;
  canManage: boolean;
  onAct: (action: JobAction) => void;
  children: ReactNode;
}) {
  const { t } = useTranslation();
  const facts: [string, string][] = [
    [t("jobs.detail.owner"), job.owner],
    [t("jobs.detail.schedule"), scheduleWords(job.schedule, t)],
    [t("jobs.detail.priority"), t(`jobs.priorities.${job.priority}`)],
    [t("jobs.detail.timeout"), durationWords(job.timeout_seconds * 1000, t)],
    [t("jobs.detail.attempts"), `${job.attempts}`],
    [t("jobs.detail.parallel"), `${job.max_parallel}`],
    [t("jobs.detail.uniqueBy"), job.unique_by],
    [
      t("jobs.detail.typical"),
      job.typical_duration_ms != null ? durationWords(job.typical_duration_ms, t) : t("jobs.detail.none"),
    ],
  ];
  return (
    <Stack gap="sm" data-job-detail>
      <Group gap="sm" align="center" wrap="wrap">
        <StateBadge state={job.state} />
        {job.state === "paused" && job.paused_at && (
          <Text size="sm" data-job-paused>
            {t("jobs.detail.pausedSince", { at: formatDateTime(job.paused_at), reason: job.pause_reason ?? "" })}
          </Text>
        )}
        {job.state === "held" && <Text size="sm">{t("jobs.detail.held")}</Text>}
      </Group>
      {canManage && actsOnRuns(job) && (
        <Group gap="sm">
          {job.state === "paused" ? (
            <Button
              variant="default"
              leftSection={<IconPlayerPlay size={16} aria-hidden="true" />}
              onClick={() => onAct("resume")}
            >
              {t("jobs.actions.resume.open")}
            </Button>
          ) : (
            <Button
              variant="default"
              leftSection={<IconPlayerPause size={16} aria-hidden="true" />}
              onClick={() => onAct("pause")}
            >
              {t("jobs.actions.pause.open")}
            </Button>
          )}
          <Button variant="default" leftSection={<IconRun size={16} aria-hidden="true" />} onClick={() => onAct("run")}>
            {t("jobs.actions.run.open")}
          </Button>
        </Group>
      )}
      <Table variant="vertical" withTableBorder maw={760}>
        <Table.Tbody>
          {facts.map(([label, value]) => (
            <Table.Tr key={label}>
              <Table.Th w={220}>{label}</Table.Th>
              <Table.Td>{value}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      {job.last_failure && (
        <Alert
          color="danger"
          title={t("jobs.detail.lastFailure", {
            at: formatDateTime(job.last_failure.finished_at ?? job.last_failure.scheduled_for),
          })}
          data-job-failure
        >
          <Text size="sm">{job.last_failure.error}</Text>
          {job.last_failure.log && (
            <Code block mt="xs">
              {job.last_failure.log}
            </Code>
          )}
        </Alert>
      )}
      {children}
    </Stack>
  );
}

/** One run in full: its times, who started it and why, its error and its log; a dead letter may run again. */
function RunDetail({ run, canManage, onRerun }: { run: JobRun; canManage: boolean; onRerun: () => void }) {
  const { t } = useTranslation();
  return (
    <Stack gap="xs" data-run-detail>
      <Group gap="sm" align="center">
        <Title order={4} size="h5">
          {t("jobs.detail.run", { at: formatDateTime(run.scheduled_for) })}
        </Title>
        <RunBadge status={run.status} />
        {canManage && rerunnable(run) && (
          <Button
            size="compact-sm"
            variant="default"
            leftSection={<IconRepeat size={14} aria-hidden="true" />}
            onClick={onRerun}
          >
            {t("jobs.actions.rerun.open")}
          </Button>
        )}
      </Group>
      <Text size="sm">
        {run.started_at
          ? t("jobs.detail.ran", {
              started: formatDateTime(run.started_at),
              duration: durationWords(run.duration_ms, t),
              attempts: run.attempts,
            })
          : t("jobs.detail.notStarted", { due: formatDateTime(run.due_at) })}
      </Text>
      {run.reason && <Text size="sm">{t("jobs.detail.startedBecause", { reason: run.reason })}</Text>}
      {run.error && (
        <Text size="sm" fw={600}>
          {run.error}
        </Text>
      )}
      <Code block>{run.log && run.log.trim() !== "" ? run.log : t("jobs.detail.noLog")}</Code>
    </Stack>
  );
}
