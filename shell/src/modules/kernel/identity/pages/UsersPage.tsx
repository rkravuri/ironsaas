import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { fetchUsers, type User } from "@/api/identity";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ListPage } from "@/components/templates";

const defaultSort = [{ field: "login_id", direction: "asc" as const }];

/** The users the reader may see (TF-101 through the grid, ADR-0031) on the list template; a row opens the user's record. */
export function UsersPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchUsers(query, signal), []);
  // Type and status in the catalogue's words, as the record page shows them; a filter that offers those words is a later item.
  const columns: DataGridColumn<User>[] = [
    { field: "login_id", headerKey: "users.loginId", sortable: true, filter: "text", flex: 2 },
    { field: "display_name", headerKey: "users.displayName", sortable: true, filter: "text", flex: 2 },
    { field: "email", headerKey: "users.email", filter: "text", flex: 2, hidden: true },
    { field: "type", headerKey: "users.type", filter: false, width: 170, format: (user) => t(`users.types.${user.type}`, { defaultValue: user.type }) },
    { field: "status", headerKey: "users.status", filter: false, width: 130, format: (user) => t(`users.statuses.${user.status}`, { defaultValue: user.status }) },
    { field: "last_login_at", headerKey: "users.lastLogin", sortable: true, filter: false, width: 190, format: (user) => (user.last_login_at ? new Date(user.last_login_at).toLocaleString() : "") },
    { field: "created_at", headerKey: "users.created", sortable: true, filter: false, width: 190, hidden: true, format: (user) => new Date(user.created_at).toLocaleString() },
  ];

  return (
    <ListPage title={t("users.title")} description={t("users.description")}>
      <DataGrid<User> columns={columns} fetchPage={fetchPage} defaultSort={defaultSort} label={t("users.title")} onRowOpen={(user) => void navigate(`/users/${user.id}`)} />
    </ListPage>
  );
}
