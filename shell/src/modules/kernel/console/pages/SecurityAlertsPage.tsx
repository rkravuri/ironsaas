import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useSearchParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Alert, Anchor, Badge, Box, Button, Code, Group, Paper, Select, Stack, Text, Title } from "@mantine/core";
import { IconCheck, IconEye, IconListSearch } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import {
  acknowledgeSecurityAlert,
  fetchAlertRules,
  fetchSecurityAlert,
  fetchSecurityAlertPage,
  resolveSecurityAlert,
  type SecurityAlert,
} from "@/api/monitoring";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ConfirmDialog, ReasonDialog } from "@/components/panels";
import { ListPage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";
import { countWords, groupWords, manageAlerts } from "./alert-words";

const newestFirst = [{ field: "raised_at", direction: "desc" as const }];
const states = ["open", "acknowledged", "resolved"] as const;
const severities = ["critical", "high", "normal"] as const;

/** A severity as a badge whose words say it, so it is never colour alone (ADR-0024). */
function SeverityBadge({ severity }: { severity: SecurityAlert["severity"] }) {
  const { t } = useTranslation();
  const color = severity === "critical" ? "danger" : severity === "high" ? "warning" : "info";
  return (
    <Badge variant="light" color={color}>
      {t(`securityAlerts.severities.${severity}`)}
    </Badge>
  );
}

/**
 * The security alerts (TF-079, ADR-0056): what the rules over the audit trail raised, newest first, open ones unless the
 * person chooses otherwise. The alert chosen in the grid shows what was counted, the link to the audit search with its
 * events and the runbook's section; a holder of monitoring.alerts.manage acknowledges it, which stops the operator's
 * page, and resolves it with a note of what they found. Both are on the record. <c>?alert=</c> opens one alert.
 */
export function SecurityAlertsPage() {
  const { t } = useTranslation();
  const session = useSession();
  const canManage = session.permissions.includes(manageAlerts);
  const [state, setState] = useState<string>("open");
  const [severity, setSeverity] = useState<string>("");
  const [rule, setRule] = useState<string>("");
  const [refreshKey, setRefreshKey] = useState(0);
  const [chosen, setSelected] = useState<SecurityAlert | undefined>();
  const [parameters] = useSearchParams();
  const named = parameters.get("alert");
  // A link may name the alert to open, such as a notification's; one chosen in the grid takes its place.
  const linked = useQuery({
    queryKey: ["security-alert", named],
    queryFn: ({ signal }) => fetchSecurityAlert(named ?? "", signal),
    enabled: named != null,
  });
  const selected = chosen ?? linked.data;
  const [acknowledging, setAcknowledging] = useState(false);
  const [resolving, setResolving] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | undefined>();
  const rules = useQuery({ queryKey: ["alert-rules"], queryFn: ({ signal }) => fetchAlertRules(signal) });

  const extra = useMemo(() => {
    const terms: string[] = [];
    const add = (field: string, value: string) => terms.push(`filter=${encodeURIComponent(`${field}:eq:${value}`)}`);
    if (state !== "") add("state", state);
    if (severity !== "") add("severity", severity);
    if (rule !== "") add("rule", rule);
    return terms;
  }, [state, severity, rule]);
  const fetchPage = useCallback(
    (query: string, signal?: AbortSignal) =>
      fetchSecurityAlertPage([query, ...extra].filter((p) => p !== "").join("&"), signal),
    [extra],
  );

  const columns: DataGridColumn<SecurityAlert>[] = [
    {
      field: "raised_at",
      headerKey: "securityAlerts.columns.raised",
      width: 235,
      sortable: true,
      format: (a) => formatDateTime(a.raised_at),
    },
    { field: "title", headerKey: "securityAlerts.columns.alert", flex: 1.4, minWidth: 260, format: (a) => a.title },
    {
      field: "severity",
      headerKey: "securityAlerts.columns.severity",
      width: 120,
      sortable: true,
      format: (a) => t(`securityAlerts.severities.${a.severity}`),
    },
    {
      field: "group_value",
      headerKey: "securityAlerts.columns.about",
      flex: 1,
      minWidth: 220,
      format: (a) => groupWords(a, t),
    },
    {
      field: "count",
      headerKey: "securityAlerts.columns.count",
      width: 230,
      sortable: true,
      format: (a) => countWords(a, t),
    },
    {
      field: "state",
      headerKey: "securityAlerts.columns.state",
      width: 140,
      sortable: true,
      format: (a) => t(`securityAlerts.states.${a.state}`),
    },
  ];

  const changed = (alert: SecurityAlert, text: string) => {
    setSelected(alert);
    setMessage({ ok: true, text });
    setRefreshKey((key) => key + 1);
  };

  const acknowledge = async () => {
    if (!selected) {
      return;
    }
    setBusy(true);
    try {
      const alert = await acknowledgeSecurityAlert(selected.id, null);
      changed(alert, t("securityAlerts.acknowledged", { title: alert.title }));
    } catch (error) {
      setMessage({ ok: false, text: error instanceof ApiError ? error.message : t("errors.unexpected") });
    } finally {
      setBusy(false);
      setAcknowledging(false);
    }
  };

  const resolve = async (note: string) => {
    if (!selected) {
      return;
    }
    const alert = await resolveSecurityAlert(selected.id, note);
    setResolving(false);
    changed(alert, t("securityAlerts.resolved", { title: alert.title }));
  };

  return (
    <>
      <ListPage
        title={t("securityAlerts.title")}
        description={t("securityAlerts.description")}
        filters={
          <Group gap="sm" align="flex-end" wrap="wrap" data-alert-filters>
            <Select
              label={t("securityAlerts.filters.state")}
              value={state}
              onChange={(value) => {
                setState(value ?? "");
                setRefreshKey((key) => key + 1);
              }}
              data={[
                { value: "", label: t("securityAlerts.filters.anyState") },
                ...states.map((s) => ({ value: s, label: t(`securityAlerts.states.${s}`) })),
              ]}
              allowDeselect={false}
              w={180}
            />
            <Select
              label={t("securityAlerts.filters.severity")}
              value={severity}
              onChange={(value) => {
                setSeverity(value ?? "");
                setRefreshKey((key) => key + 1);
              }}
              data={[
                { value: "", label: t("securityAlerts.filters.anySeverity") },
                ...severities.map((s) => ({ value: s, label: t(`securityAlerts.severities.${s}`) })),
              ]}
              allowDeselect={false}
              w={180}
            />
            <Select
              label={t("securityAlerts.filters.rule")}
              value={rule}
              onChange={(value) => {
                setRule(value ?? "");
                setRefreshKey((key) => key + 1);
              }}
              data={[
                { value: "", label: t("securityAlerts.filters.anyRule") },
                ...(rules.data?.data ?? []).map((r) => ({ value: r.name, label: r.title })),
              ]}
              allowDeselect={false}
              searchable
              w={340}
            />
          </Group>
        }
      >
        <Stack gap="md">
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
          <Box h={420}>
            <DataGrid<SecurityAlert>
              columns={columns}
              fetchPage={fetchPage}
              defaultSort={newestFirst}
              label={t("securityAlerts.gridLabel")}
              refreshKey={refreshKey}
              onSelectionChange={setSelected}
            />
          </Box>
          {selected ? (
            <AlertDetail
              alert={selected}
              canManage={canManage}
              onAcknowledge={() => setAcknowledging(true)}
              onResolve={() => setResolving(true)}
            />
          ) : (
            <Text size="sm" c="dimmed">
              {t("securityAlerts.choose")}
            </Text>
          )}
        </Stack>
      </ListPage>
      {selected && (
        <ConfirmDialog
          opened={acknowledging}
          title={t("securityAlerts.acknowledgeTitle")}
          message={t("securityAlerts.acknowledgeMessage", { title: selected.title })}
          confirmLabel={t("securityAlerts.acknowledge")}
          busy={busy}
          onConfirm={() => void acknowledge()}
          onCancel={() => setAcknowledging(false)}
        />
      )}
      {selected && resolving && (
        <ReasonDialog
          opened
          title={t("securityAlerts.resolveTitle")}
          message={t("securityAlerts.resolveMessage", { title: selected.title })}
          reasonLabel={t("securityAlerts.note")}
          reasonHint={t("securityAlerts.noteHint")}
          confirmLabel={t("securityAlerts.resolve")}
          onSubmit={resolve}
          onClose={() => setResolving(false)}
        />
      )}
    </>
  );
}

/** One alert in full: what it counted, when, the links to its events and its runbook, and what was done. */
function AlertDetail({
  alert,
  canManage,
  onAcknowledge,
  onResolve,
}: {
  alert: SecurityAlert;
  canManage: boolean;
  onAcknowledge: () => void;
  onResolve: () => void;
}) {
  const { t } = useTranslation();
  return (
    <Paper component="section" withBorder p="md" data-alert-detail aria-labelledby="alert-detail-title">
      <Stack gap="sm">
        <Group justify="space-between" align="flex-start" wrap="wrap">
          <Stack gap={4}>
            <Title order={3} id="alert-detail-title" size="h4">
              {alert.title}
            </Title>
            <Group gap="xs">
              <SeverityBadge severity={alert.severity} />
              <Badge variant="outline">{t(`securityAlerts.states.${alert.state}`)}</Badge>
            </Group>
          </Stack>
          {canManage && alert.origin === "local" && alert.state !== "resolved" && (
            <Group gap="xs">
              <Button
                variant="default"
                leftSection={<IconEye size={16} aria-hidden="true" />}
                disabled={alert.state !== "open"}
                onClick={onAcknowledge}
              >
                {t("securityAlerts.acknowledge")}
              </Button>
              <Button leftSection={<IconCheck size={16} aria-hidden="true" />} onClick={onResolve}>
                {t("securityAlerts.resolve")}
              </Button>
            </Group>
          )}
        </Group>
        <Text size="sm">
          {alert.kind === "failing"
            ? (alert.reason ?? t("securityAlerts.failingCount"))
            : t("securityAlerts.detail", {
                about: groupWords(alert, t),
                counted: countWords(alert, t),
                first: alert.first_event_at ? formatDateTime(alert.first_event_at) : "",
                last: alert.last_event_at ? formatDateTime(alert.last_event_at) : "",
              })}
        </Text>
        <Group gap="lg" wrap="wrap">
          {alert.link && (
            <Anchor component={Link} to={alert.link} size="sm">
              <Group gap={4} component="span">
                <IconListSearch size={16} aria-hidden="true" />
                {alert.kind === "failing" ? t("securityAlerts.openRule") : t("securityAlerts.openEvents")}
              </Group>
            </Anchor>
          )}
          {alert.runbook && (
            <Text size="sm">
              {t("securityAlerts.runbook")} <Code>{alert.runbook}</Code>
            </Text>
          )}
        </Group>
        {alert.acknowledged_at && (
          <Text size="sm" c="dimmed">
            {t("securityAlerts.acknowledgedBy", {
              name: alert.acknowledged_by ?? t("securityAlerts.someone"),
              at: formatDateTime(alert.acknowledged_at),
            })}
          </Text>
        )}
        {alert.resolved_at && (
          <Text size="sm" c="dimmed">
            {t("securityAlerts.resolvedBy", {
              name: alert.resolved_by ?? t("securityAlerts.someone"),
              at: formatDateTime(alert.resolved_at),
              note: alert.note ?? "",
            })}
          </Text>
        )}
      </Stack>
    </Paper>
  );
}
