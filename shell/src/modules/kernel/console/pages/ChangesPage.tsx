import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate, useSearchParams } from "react-router";
import { SegmentedControl } from "@mantine/core";
import { changeViews, fetchChanges, type ChangeView, type PendingChange } from "@/api/changes";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ListPage } from "@/components/templates";
import { Mandala } from "@/design/patterns";
import { formatDateTime } from "@/i18n/dates";
import { areaWords, changeStateWords } from "./change-words";

const readAll = "changes.changes.read";

const isView = (value: string | null): value is ChangeView => changeViews.some((view) => view === value);

/** The view the address asks for, when the person may have it; otherwise the changes they may approve. */
const viewOf = (asked: string | null, readsAll: boolean): ChangeView =>
  isView(asked) && (asked !== "all" || readsAll) ? asked : "to_approve";

/**
 * Changes of sensitive configuration (TF-068, ADR-0052) on the list template: the ones the person may approve, their
 * own, and with changes.changes.read every one. A change opens on its record page, with its before and after side by
 * side. The view is in the address, so the frame's notice and a colleague's link open the right one.
 */
export function ChangesPage() {
  const { t } = useTranslation();
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const readsAll = session.permissions.includes(readAll);
  const views = changeViews.filter((view) => view !== "all" || readsAll);
  const view = viewOf(params.get("view"), readsAll);

  return (
    <ListPage
      title={t("changes.title")}
      description={t("changes.description")}
      drawing={<Mandala symmetry={8} rings={2} stroke={2} density={0.5} seed="changes" size={56} accent="console" />}
      filters={
        <SegmentedControl
          aria-label={t("changes.which")}
          value={view}
          onChange={(value) => {
            if (isView(value)) {
              setParams(value === "to_approve" ? {} : { view: value }, { replace: true });
            }
          }}
          data={views.map((value) => ({ value, label: t(`changes.views.${value}`) }))}
        />
      }
    >
      <ChangesGrid key={view} view={view} />
    </ListPage>
  );
}

/** One view's changes, newest first; a change opens on its record page. */
function ChangesGrid({ view }: { view: ChangeView }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchChanges(view, query, signal), [view]);
  const columns: DataGridColumn<PendingChange>[] = [
    { field: "title", headerKey: "changes.columns.title", flex: 2, minWidth: 260 },
    {
      field: "area",
      headerKey: "changes.columns.area",
      width: 210,
      format: (change) => areaWords(change.area, t),
    },
    {
      field: "status",
      headerKey: "changes.columns.status",
      width: 140,
      format: (change) => changeStateWords(change.status, t),
    },
    { field: "requester_name", headerKey: "changes.columns.requester", flex: 1, minWidth: 170 },
    {
      field: "requested_at",
      headerKey: "changes.columns.requested",
      width: 215,
      format: (change) => formatDateTime(change.requested_at),
    },
    {
      field: "expires_at",
      headerKey: "changes.columns.expires",
      width: 215,
      format: (change) => (change.status === "pending" ? formatDateTime(change.expires_at) : ""),
    },
  ];
  return (
    <DataGrid<PendingChange>
      columns={columns}
      fetchPage={fetchPage}
      label={t(`changes.gridLabel.${view}`)}
      onRowOpen={(change) => void navigate(`/console/changes/${change.id}`)}
    />
  );
}
