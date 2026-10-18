import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import { Stack, Title } from "@mantine/core";
import { fetchUsers, type User } from "@/api/identity";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";

const columns: DataGridColumn<User>[] = [
  { field: "login_id", headerKey: "users.loginId", sortable: true, filter: "text", flex: 2 },
  { field: "display_name", headerKey: "users.displayName", sortable: true, filter: "text", flex: 2 },
  { field: "email", headerKey: "users.email", filter: "text", flex: 2, hidden: true },
  { field: "type", headerKey: "users.type", filter: "text", width: 150 },
  { field: "status", headerKey: "users.status", filter: "text", width: 130 },
  { field: "last_login_at", headerKey: "users.lastLogin", sortable: true, filter: false, width: 190, format: (user) => (user.last_login_at ? new Date(user.last_login_at).toLocaleString() : "") },
  { field: "created_at", headerKey: "users.created", sortable: true, filter: false, width: 190, hidden: true, format: (user) => new Date(user.created_at).toLocaleString() },
];

const defaultSort = [{ field: "login_id", direction: "asc" as const }];

/** The users of this tenant (TF-101 through the grid): server paging, sorting and filtering, from the API's fields. */
export function UsersPage() {
  const { t } = useTranslation();
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchUsers(query, signal), []);

  return (
    <Stack style={{ height: "calc(100dvh - var(--app-shell-header-height) - 2 * var(--app-shell-padding))" }}>
      <Title order={2}>{t("users.title")}</Title>
      <DataGrid<User> columns={columns} fetchPage={fetchPage} defaultSort={defaultSort} label={t("users.title")} />
    </Stack>
  );
}
