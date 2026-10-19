import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";
import { fetchUsers, type User } from "@/api/identity";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ListPage } from "@/components/templates";
import { UserDialog } from "./UserDialog";

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

/** The users of this tenant (TF-101 through the grid) on the list template; a row opens the user's details. */
export function UsersPage() {
  const { t } = useTranslation();
  const [open, setOpen] = useState<User | undefined>();
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchUsers(query, signal), []);

  return (
    <>
      <ListPage title={t("users.title")} description={t("users.description")}>
        <DataGrid<User> columns={columns} fetchPage={fetchPage} defaultSort={defaultSort} label={t("users.title")} onRowOpen={setOpen} />
      </ListPage>
      <UserDialog user={open} onClose={() => setOpen(undefined)} />
    </>
  );
}
