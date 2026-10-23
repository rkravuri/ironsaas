import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Stack, Tabs, Text } from "@mantine/core";
import { IconPlus } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import {
  deleteConflictRule,
  fetchConflictRules,
  fetchConflicts,
  fetchLatestConflictRun,
  runConflictReport,
  type ConflictEntry,
  type ConflictRule,
} from "@/api/authorization";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ConfirmDialog, RecordHistoryButton } from "@/components/panels";
import { ListPage } from "@/components/templates";
import { Mandala } from "@/design/patterns";
import { ConflictRuleDialog } from "./ConflictRuleDialog";
import { conflictGrantWords, ruleSidesWords } from "./conflict-words";
import { formatDateTime } from "@/i18n/dates";
import { useReadsAuditTrail } from "@/auth/record-history";

const ruleSort = [{ field: "name", direction: "asc" as const }];
const entrySort = [{ field: "principal_name", direction: "asc" as const }];

/**
 * Segregation of duties (TF-036, ADR-0039): the tenant's conflict rules, each two rights one person should not hold
 * over the same unit, with what the rule does in words; and the report of the conflicts that exist, when it last ran,
 * and running it now. Reading needs authz.sod.read; changing a rule and running the report, authz.sod.manage.
 */
export function ConflictsPage() {
  const { t } = useTranslation();
  const session = useSession();
  const canManage = session.tenantPermissions.includes("authz.sod.manage");
  const readsTrail = useReadsAuditTrail();
  const [tab, setTab] = useState<string | null>("rules");
  // The editor keeps its rule while it closes, so its title stays the same until it is gone.
  const [editor, setEditor] = useState<{
    opened: boolean;
    rule: ConflictRule | undefined;
  }>({ opened: false, rule: undefined });
  const [removing, setRemoving] = useState<ConflictRule | undefined>();
  const [selected, setSelected] = useState<ConflictRule | undefined>();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();
  const [refreshKey, setRefreshKey] = useState(0);
  const latest = useQuery({
    queryKey: ["authz", "conflict-report", "latest", refreshKey],
    queryFn: ({ signal }) => fetchLatestConflictRun(signal),
  });
  const fetchRules = useCallback((query: string, signal?: AbortSignal) => fetchConflictRules(query, signal), []);
  const fetchEntries = useCallback((query: string, signal?: AbortSignal) => fetchConflicts(query, signal), []);

  const changed = () => {
    setSelected(undefined);
    setRefreshKey((key) => key + 1);
  };

  const act = async (work: () => Promise<unknown>) => {
    setBusy(true);
    setFailure(undefined);
    try {
      await work();
      changed();
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
    }
  };

  const ruleColumns: DataGridColumn<ConflictRule>[] = [
    {
      field: "name",
      headerKey: "conflicts.rules.columns.name",
      sortable: true,
      filter: "text",
      flex: 2,
    },
    {
      field: "first",
      headerKey: "conflicts.rules.columns.sides",
      flex: 3,
      format: (rule) => ruleSidesWords(t, rule),
    },
    {
      field: "mode",
      headerKey: "conflicts.rules.columns.mode",
      width: 140,
      format: (rule) => t(`conflicts.modes.${rule.mode}`),
    },
    { field: "reason", headerKey: "conflicts.rules.columns.reason", flex: 3 },
    {
      field: "owner_name",
      headerKey: "conflicts.rules.columns.owner",
      flex: 1,
      format: (rule) => rule.owner_name ?? "",
    },
  ];
  const entryColumns: DataGridColumn<ConflictEntry>[] = [
    {
      field: "principal_name",
      headerKey: "conflicts.report.columns.person",
      sortable: true,
      filter: "text",
      flex: 2,
    },
    {
      field: "rule_name",
      headerKey: "conflicts.report.columns.rule",
      sortable: true,
      filter: "text",
      flex: 2,
    },
    {
      field: "mode",
      headerKey: "conflicts.report.columns.mode",
      width: 130,
      format: (entry) => t(`conflicts.modes.${entry.mode}`),
    },
    {
      field: "first",
      headerKey: "conflicts.report.columns.first",
      flex: 3,
      format: (entry) => conflictGrantWords(t, entry.first),
    },
    {
      field: "second",
      headerKey: "conflicts.report.columns.second",
      flex: 3,
      format: (entry) => conflictGrantWords(t, entry.second),
    },
    {
      field: "unit_name",
      headerKey: "conflicts.report.columns.unit",
      flex: 2,
      format: (entry) => entry.unit_name ?? t("conflicts.report.wholeTenant"),
    },
  ];

  const run = latest.data?.run;
  const lastRun = run
    ? t("conflicts.report.last", {
        when: formatDateTime(run.computed_at),
        how: t(run.trigger === "daily" ? "conflicts.report.daily" : "conflicts.report.onDemand"),
        count: run.conflicts,
        rules: run.rules,
      })
    : t("conflicts.report.never");

  const history = readsTrail && (
    <RecordHistoryButton
      record={selected ? { recordType: "conflict_rule", recordId: selected.id } : undefined}
      name={selected?.name ?? ""}
    />
  );
  const actions =
    tab === "rules" ? (
      canManage ? (
        <>
          {history}
          <Button variant="default" disabled={!selected} onClick={() => selected && setRemoving(selected)}>
            {t("conflicts.rules.remove")}
          </Button>
          <Button
            variant="default"
            disabled={!selected}
            onClick={() => selected && setEditor({ opened: true, rule: selected })}
          >
            {t("conflicts.rules.edit")}
          </Button>
          <Button
            leftSection={<IconPlus size={16} aria-hidden="true" />}
            onClick={() => setEditor({ opened: true, rule: undefined })}
          >
            {t("conflicts.rules.add")}
          </Button>
        </>
      ) : (
        history || undefined
      )
    ) : canManage ? (
      <Button loading={busy} onClick={() => void act(runConflictReport)}>
        {t("conflicts.report.run")}
      </Button>
    ) : undefined;

  return (
    <>
      <ListPage
        title={t("conflicts.title")}
        description={t("conflicts.description")}
        drawing={
          <Mandala symmetry={8} rings={3} stroke={2} density={0.55} seed="conflicts" size={56} accent="console" />
        }
        actions={actions}
        {...(failure
          ? {
              filters: (
                <Alert
                  color="danger"
                  role="alert"
                  w="100%"
                  withCloseButton
                  closeButtonLabel={t("dialog.close")}
                  onClose={() => setFailure(undefined)}
                >
                  {failure.message}
                  {failure.correlationId !== undefined && (
                    <Text size="xs" c="dimmed" mt={4}>
                      {t("errors.correlation", { id: failure.correlationId })}
                    </Text>
                  )}
                </Alert>
              ),
            }
          : {})}
      >
        <Tabs
          value={tab}
          onChange={(next) => {
            // The rules grid starts again on its tab, so a choice made before leaving it no longer stands.
            setTab(next);
            setSelected(undefined);
          }}
          keepMounted={false}
        >
          <Tabs.List>
            <Tabs.Tab value="rules">{t("conflicts.rules.title")}</Tabs.Tab>
            <Tabs.Tab value="report">{t("conflicts.report.title")}</Tabs.Tab>
          </Tabs.List>
          {/* A grid inside a tab has no height to fill, so each has its own, as on the review page. */}
          <Tabs.Panel value="rules" pt="md">
            <div style={{ height: 460 }}>
              <DataGrid<ConflictRule>
                columns={ruleColumns}
                fetchPage={fetchRules}
                defaultSort={ruleSort}
                label={t("conflicts.rules.title")}
                refreshKey={refreshKey}
                onSelectionChange={setSelected}
              />
            </div>
          </Tabs.Panel>
          <Tabs.Panel value="report" pt="md">
            <Stack gap="sm">
              <Text size="sm" data-last-run>
                {lastRun}
              </Text>
              <div style={{ height: 460 }}>
                <DataGrid<ConflictEntry>
                  columns={entryColumns}
                  fetchPage={fetchEntries}
                  defaultSort={entrySort}
                  label={t("conflicts.report.title")}
                  refreshKey={refreshKey}
                />
              </div>
            </Stack>
          </Tabs.Panel>
        </Tabs>
      </ListPage>
      <ConflictRuleDialog
        opened={editor.opened}
        rule={editor.rule}
        onClose={() => setEditor((current) => ({ ...current, opened: false }))}
        onDone={async () => {
          setEditor((current) => ({ ...current, opened: false }));
          changed();
          await Promise.resolve();
        }}
      />
      <ConfirmDialog
        opened={removing !== undefined}
        title={t("conflicts.remove.title", { name: removing?.name ?? "" })}
        message={t("conflicts.remove.message")}
        confirmLabel={t("conflicts.remove.confirm")}
        danger
        busy={busy}
        onConfirm={() => {
          const rule = removing;
          setRemoving(undefined);
          if (rule) {
            void act(() => deleteConflictRule(rule.id));
          }
        }}
        onCancel={() => setRemoving(undefined)}
      />
    </>
  );
}
