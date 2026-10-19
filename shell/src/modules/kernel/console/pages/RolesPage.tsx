import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate } from "react-router";
import { Alert, Button, Text } from "@mantine/core";
import { IconDownload, IconPlus, IconUpload } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { exportRoles, fetchRoles, type Role } from "@/api/authorization";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ListPage } from "@/components/templates";
import { Mandala } from "@/design/patterns";
import { ImportRolesDialog } from "./ImportRolesDialog";

const manage = "authz.roles.manage";
const defaultSort = [{ field: "code", direction: "asc" as const }];

/** Saves the export as a file the person keeps, then hands it to another tenant's import. */
function download(document: unknown) {
  const blob = new Blob([JSON.stringify(document, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = window.document.createElement("a");
  link.href = url;
  link.download = "roles.json";
  link.click();
  URL.revokeObjectURL(url);
}

/** The roles of this tenant (TF-032) on the list template: system roles first by their code, a row opens the role. */
export function RolesPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const session = useSession();
  const canManage = session.tenantPermissions.includes(manage);
  const [importing, setImporting] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);
  const [failure, setFailure] = useState<ApiError | undefined>();
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchRoles(query, signal), []);

  const columns: DataGridColumn<Role>[] = [
    { field: "name", headerKey: "roles.name", sortable: true, filter: "text", flex: 2 },
    { field: "code", headerKey: "roles.code", sortable: true, filter: "text", flex: 2 },
    { field: "principal_type", headerKey: "roles.principalType", filter: false, width: 190, format: (role) => t(`roles.principalTypes.${role.principal_type}`) },
    { field: "is_system", headerKey: "roles.kind", filter: false, width: 150, format: (role) => t(role.is_system ? "roles.kinds.system" : "roles.kinds.tenant") },
    { field: "permissions", headerKey: "roles.permissions", filter: false, width: 150, format: (role) => t("roles.permissionCount", { count: role.permissions.length }) },
    { field: "updated_at", headerKey: "roles.updated", sortable: true, filter: false, width: 190, hidden: true, format: (role) => new Date(role.updated_at).toLocaleString() },
  ];

  const exportAll = async () => {
    setFailure(undefined);
    try {
      download(await exportRoles());
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    }
  };

  return (
    <>
      <ListPage
        title={t("roles.title")}
        description={t("roles.description")}
        drawing={<Mandala symmetry={6} rings={3} stroke={2} density={0.5} seed="roles" size={56} accent="console" />}
        actions={
          <>
            <Button variant="default" leftSection={<IconDownload size={16} aria-hidden="true" />} onClick={() => void exportAll()}>
              {t("roles.export")}
            </Button>
            {canManage && (
              <Button variant="default" leftSection={<IconUpload size={16} aria-hidden="true" />} onClick={() => setImporting(true)}>
                {t("roles.import.open")}
              </Button>
            )}
            {canManage && (
              <Button component={Link} to="/console/roles/new" leftSection={<IconPlus size={16} aria-hidden="true" />}>
                {t("roles.new")}
              </Button>
            )}
          </>
        }
        {...(failure
          ? {
              filters: (
                <Alert color="danger" role="alert" w="100%">
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
        <DataGrid<Role>
          columns={columns}
          fetchPage={fetchPage}
          defaultSort={defaultSort}
          label={t("roles.title")}
          refreshKey={refreshKey}
          onRowOpen={(role) => void navigate(`/console/roles/${role.id}`)}
        />
      </ListPage>
      {importing && (
        <ImportRolesDialog
          onClose={() => setImporting(false)}
          onApplied={() => {
            setImporting(false);
            setRefreshKey((key) => key + 1);
          }}
        />
      )}
    </>
  );
}
