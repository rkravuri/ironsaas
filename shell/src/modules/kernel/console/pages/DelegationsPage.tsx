import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Badge, Button, Group, Stack, Text } from "@mantine/core";
import { IconPlus, IconUserShare } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { endDelegation, fetchManagedDelegations, fetchMyDelegations, type Delegation } from "@/api/authorization";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ConfirmDialog, EmptyState, LoadingState } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { dutyList } from "@/shell/acting";
import { DelegationDialog } from "./DelegationDialog";
import { isRunning, periodLabel, statusTone } from "./delegation-words";

const manage = "authz.delegations.manage";
const mineKey = ["authz", "delegations", "mine"] as const;
const managedSort = [{ field: "starts_at", direction: "desc" as const }];

/**
 * Delegations (TF-037, ADR-0037) on the console template. Everyone sees the delegations they give and hold, delegates
 * their own duties, ends one they are part of, and acts for a delegator whose delegation is active. An administrator
 * with authz.delegations.manage also sees, delegates and ends those of the people of their units.
 */
export function DelegationsPage() {
  const { t } = useTranslation();
  const session = useSession();
  const queryClient = useQueryClient();
  const [creating, setCreating] = useState<"mine" | "someone" | undefined>();
  const [ending, setEnding] = useState<Delegation | undefined>();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();
  const [refreshKey, setRefreshKey] = useState(0);
  const [selected, setSelected] = useState<Delegation | undefined>();
  const canManage = session.permissions.includes(manage);

  const changed = async () => {
    setRefreshKey((key) => key + 1);
    // /me answers the active delegations both ways, which the header's control follows.
    await Promise.all([queryClient.invalidateQueries({ queryKey: mineKey }), session.refresh()]);
  };

  const end = async () => {
    if (!ending) {
      return;
    }
    setBusy(true);
    setFailure(undefined);
    try {
      await endDelegation(ending.id);
      await changed();
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
      setEnding(undefined);
    }
  };

  const sections = [
    {
      id: "yours",
      title: t("delegations.yours.title"),
      description: t("delegations.yours.description"),
      children: <DelegationList onCreate={() => setCreating("mine")} onEnd={setEnding} />,
    },
    ...(canManage
      ? [
          {
            id: "others",
            title: t("delegations.others.title"),
            description: t("delegations.others.description"),
            children: <Others refreshKey={refreshKey} selected={selected} onSelect={setSelected} onCreate={() => setCreating("someone")} onEnd={setEnding} />,
          },
        ]
      : []),
  ];

  return (
    <>
      {failure && (
        <Alert color="danger" role="alert" mb="md" withCloseButton closeButtonLabel={t("dialog.close")} onClose={() => setFailure(undefined)}>
          {failure.message}
          {failure.correlationId !== undefined && (
            <Text size="xs" c="dimmed" mt={4}>
              {t("errors.correlation", { id: failure.correlationId })}
            </Text>
          )}
        </Alert>
      )}
      <ConsolePage title={t("delegations.title")} description={t("delegations.description")} sections={sections} />
      <DelegationDialog
        opened={creating !== undefined}
        forSomeone={creating === "someone"}
        onClose={() => setCreating(undefined)}
        onDone={async () => {
          setCreating(undefined);
          await changed();
        }}
      />
      <ConfirmDialog
        opened={ending !== undefined}
        title={t("delegations.end.title")}
        message={ending ? t("delegations.end.message", { delegator: ending.delegator.name, delegate: ending.delegate.name }) : ""}
        confirmLabel={t("delegations.end.confirm")}
        danger
        busy={busy}
        onConfirm={() => void end()}
        onCancel={() => setEnding(undefined)}
      />
    </>
  );
}

/**
 * The delegations the person gives and holds, each a sentence with its period, reason and status in words, and Act for
 * on an active one held. With onCreate and onEnd, also delegating and ending; My profile shows the list without them.
 */
export function DelegationList({ onCreate, onEnd }: { onCreate?: () => void; onEnd?: (delegation: Delegation) => void }) {
  const { t, i18n } = useTranslation();
  const session = useSession();
  const mine = useQuery({ queryKey: mineKey, queryFn: ({ signal }) => fetchMyDelegations(signal) });
  const [acting, setActing] = useState<string | undefined>();
  const [failure, setFailure] = useState<ApiError | undefined>();
  const me = session.me;
  const actFor = async (delegator: string) => {
    setActing(delegator);
    setFailure(undefined);
    try {
      await session.actFor(delegator);
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setActing(undefined);
    }
  };

  return (
    <Stack gap="md">
      <Group justify="space-between" align="flex-start">
        <Text size="sm" c="dimmed" maw={560}>
          {t("delegations.yours.intro")}
        </Text>
        {onCreate && (
          <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={onCreate} disabled={session.me?.acting_for != null}>
            {t("delegations.create.open")}
          </Button>
        )}
      </Group>
      {onCreate && session.me?.acting_for != null && (
        <Text size="sm" c="dimmed">
          {t("delegations.yours.whileActing")}
        </Text>
      )}
      {failure && (
        <Alert color="danger" role="alert">
          {failure.message}
        </Alert>
      )}
      {mine.isPending ? (
        <LoadingState height={120} />
      ) : mine.isError ? (
        <Alert color="danger" role="alert" title={t("delegations.failed")}>
          {mine.error instanceof ApiError ? mine.error.message : t("errors.unexpected")}
        </Alert>
      ) : mine.data.data.length === 0 ? (
        <EmptyState drawing="nothingYet" size="sm" title={t("delegations.yours.none")} text={t("delegations.yours.noneText")} />
      ) : (
        <Stack gap="sm" component="ul" m={0} pl={0} style={{ listStyle: "none" }} aria-label={t("delegations.yours.title")}>
          {mine.data.data.map((delegation) => {
            const given = delegation.delegator.id === me?.id;
            const period = periodLabel(delegation.starts_at, delegation.ends_at, i18n.language);
            const duties = dutyList(delegation.duties, i18n.language);
            const canAct = !given && delegation.status === "active" && session.me?.acting_for?.id !== delegation.delegator.id;
            return (
              <li key={delegation.id} data-delegation={delegation.id}>
                <Group justify="space-between" align="flex-start" wrap="nowrap" gap="md">
                  <Stack gap={2}>
                    <Text size="sm">
                      {given ? t("delegations.yours.given", { name: delegation.delegate.name, duties }) : t("delegations.yours.received", { name: delegation.delegator.name, duties })}
                    </Text>
                    <Text size="xs" c="dimmed">
                      {t("delegations.period", period)} {t("delegations.reason", { reason: delegation.reason })}
                    </Text>
                  </Stack>
                  <Group gap="xs" wrap="nowrap">
                    <StatusBadge delegation={delegation} />
                    {canAct && (
                      <Button size="xs" variant="light" leftSection={<IconUserShare size={14} aria-hidden="true" />} loading={acting === delegation.delegator.id} onClick={() => void actFor(delegation.delegator.id)}>
                        {t("delegations.actFor", { name: delegation.delegator.name })}
                      </Button>
                    )}
                    {onEnd && isRunning(delegation.status) && (
                      <Button size="xs" variant="default" onClick={() => onEnd(delegation)}>
                        {t("delegations.end.open")}
                      </Button>
                    )}
                  </Group>
                </Group>
              </li>
            );
          })}
        </Stack>
      )}
    </Stack>
  );
}

function StatusBadge({ delegation }: { delegation: Delegation }) {
  const { t } = useTranslation();
  const tone = statusTone[delegation.status];
  return (
    <Badge variant={tone === "neutral" ? "default" : "light"} {...(tone !== "neutral" ? { color: tone } : {})} radius="sm">
      {t(`delegations.status.${delegation.status}`)}
    </Badge>
  );
}

/** Others' delegations, for an administrator: the grid, delegating for someone, and ending the one chosen. */
function Others({
  refreshKey,
  selected,
  onSelect,
  onCreate,
  onEnd,
}: {
  refreshKey: number;
  selected: Delegation | undefined;
  onSelect: (delegation: Delegation | undefined) => void;
  onCreate: () => void;
  onEnd: (delegation: Delegation) => void;
}) {
  const { t, i18n } = useTranslation();
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchManagedDelegations(query, signal), []);
  const columns: DataGridColumn<Delegation>[] = [
    { field: "delegator", headerKey: "delegations.columns.delegator", flex: 2, format: (row) => row.delegator.name },
    { field: "delegate", headerKey: "delegations.columns.delegate", flex: 2, format: (row) => row.delegate.name },
    { field: "duties", headerKey: "delegations.columns.duties", flex: 2, format: (row) => dutyList(row.duties, i18n.language) },
    { field: "starts_at", headerKey: "delegations.columns.from", sortable: true, width: 150, format: (row) => periodLabel(row.starts_at, row.ends_at, i18n.language).from },
    { field: "ends_at", headerKey: "delegations.columns.until", sortable: true, width: 150, format: (row) => periodLabel(row.starts_at, row.ends_at, i18n.language).until },
    { field: "status", headerKey: "delegations.columns.status", width: 140, format: (row) => t(`delegations.status.${row.status}`) },
    { field: "reason", headerKey: "delegations.columns.reason", flex: 2, hidden: true },
  ];
  return (
    <Stack gap="sm">
      <Group justify="flex-end" gap="xs">
        <Button variant="default" disabled={!selected || !isRunning(selected.status)} onClick={() => selected && onEnd(selected)}>
          {t("delegations.others.endSelected")}
        </Button>
        <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={onCreate}>
          {t("delegations.others.create")}
        </Button>
      </Group>
      <div style={{ height: 420 }}>
        <DataGrid<Delegation> columns={columns} fetchPage={fetchPage} defaultSort={managedSort} label={t("delegations.others.title")} refreshKey={refreshKey} onSelectionChange={onSelect} />
      </div>
    </Stack>
  );
}
