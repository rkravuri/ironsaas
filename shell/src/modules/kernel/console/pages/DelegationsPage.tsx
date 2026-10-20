import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Text } from "@mantine/core";
import { IconPlus } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { endDelegation, fetchManagedDelegations, type Delegation } from "@/api/authorization";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ListPage } from "@/components/templates";
import { Mandala } from "@/design/patterns";
import { dutyList } from "@/shell/acting";
import { DelegationDialog } from "./DelegationDialog";
import { isRunning, periodLabel } from "./delegation-words";
import { EndDelegationDialog } from "./PersonalDelegations";

const managedSort = [{ field: "starts_at", direction: "desc" as const }];

/**
 * Others' delegations (TF-037, ADR-0037), for administrators with authz.delegations.manage: those of the people homed
 * at the units where they hold it, in the grid; delegating for one of them; and ending the one chosen. A person's own
 * delegations are in My profile.
 */
export function DelegationsPage() {
  const { t, i18n } = useTranslation();
  const session = useSession();
  const [creating, setCreating] = useState(false);
  const [ending, setEnding] = useState<Delegation | undefined>();
  const [selected, setSelected] = useState<Delegation | undefined>();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();
  const [refreshKey, setRefreshKey] = useState(0);
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchManagedDelegations(query, signal), []);

  const changed = async () => {
    setSelected(undefined);
    setRefreshKey((key) => key + 1);
    // The administrator may be a delegator or a delegate too: /me answers the active delegations both ways.
    await session.refresh();
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
    <>
      <ListPage
        title={t("delegations.title")}
        description={t("delegations.description")}
        drawing={<Mandala symmetry={12} rings={2} stroke={2} density={0.5} seed="delegations" size={56} accent="console" />}
        actions={
          <>
            <Button variant="default" disabled={!selected || !isRunning(selected.status)} onClick={() => selected && setEnding(selected)}>
              {t("delegations.endSelected")}
            </Button>
            <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={() => setCreating(true)}>
              {t("delegations.createForSomeone")}
            </Button>
          </>
        }
        {...(failure
          ? {
              filters: (
                <Alert color="danger" role="alert" w="100%" withCloseButton closeButtonLabel={t("dialog.close")} onClose={() => setFailure(undefined)}>
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
        <DataGrid<Delegation> columns={columns} fetchPage={fetchPage} defaultSort={managedSort} label={t("delegations.title")} refreshKey={refreshKey} onSelectionChange={setSelected} />
      </ListPage>
      <DelegationDialog
        opened={creating}
        forSomeone
        onClose={() => setCreating(false)}
        onDone={async () => {
          setCreating(false);
          await changed();
        }}
      />
      <EndDelegationDialog delegation={ending} busy={busy} onConfirm={() => void end()} onCancel={() => setEnding(undefined)} />
    </>
  );
}
