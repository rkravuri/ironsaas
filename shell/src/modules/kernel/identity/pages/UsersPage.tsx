import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate, useSearchParams } from "react-router";
import { Button } from "@mantine/core";
import { IconUserPlus } from "@tabler/icons-react";
import { fetchUsers, type User } from "@/api/identity";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ListPage } from "@/components/templates";
import { workingScope } from "@/shell/working-unit";
import { InviteDialog } from "./InviteDialog";

const defaultSort = [{ field: "login_id", direction: "asc" as const }];

/**
 * The users the reader may see (TF-101 through the grid, ADR-0031) on the list template; a row opens the user's record.
 * "Invite a person" shows where the reader manages users somewhere; the dialog asks /me/access at the unit it picks.
 * The quick action of the home page and the search arrives as ?invite, which opens the dialog once. For a person with
 * several units, the list keeps to the unit they work for and the units below it, or shows all of them, and the
 * description says which (ADR-0038); choosing another unit in the header reads the list again.
 */
export function UsersPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const canInvite = session.permissions.includes("identity.users.manage");
  // Asked for by the button or by ?invite; shown while the person may invite at all.
  const [requested, setRequested] = useState(() => params.has("invite"));
  const inviting = requested && canInvite;
  useEffect(() => {
    if (params.has("invite")) {
      const rest = new URLSearchParams(params);
      rest.delete("invite");
      setParams(rest, { replace: true });
    }
  }, [params, setParams]);
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchUsers(query, signal), []);
  const scope = workingScope(session.me);
  const scopeWords = scope === undefined ? undefined : scope.kind === "all" ? t("users.scope.all") : t("users.scope.unit", { unit: scope.name });
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
    <ListPage
      title={t("users.title")}
      description={scopeWords === undefined ? t("users.description") : `${t("users.description")} ${scopeWords}`}
      actions={
        canInvite ? (
          <Button leftSection={<IconUserPlus size={16} aria-hidden="true" />} onClick={() => setRequested(true)}>
            {t("users.invite.open")}
          </Button>
        ) : undefined
      }
    >
      <DataGrid<User>
        columns={columns}
        fetchPage={fetchPage}
        defaultSort={defaultSort}
        label={t("users.title")}
        refreshKey={`${session.me?.working_unit?.id ?? ""}:${String(session.me?.all_units ?? false)}`}
        onRowOpen={(user) => void navigate(`/users/${user.id}`)}
      />
      {inviting && <InviteDialog onClose={() => setRequested(false)} />}
    </ListPage>
  );
}
