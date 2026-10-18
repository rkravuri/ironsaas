import { useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Stack, Text, Title } from "@mantine/core";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { pageOf, sampleRows, type SampleRow } from "./sample-rows";

const columns: DataGridColumn<SampleRow>[] = [
  { field: "name", headerKey: "gallery.grid.name", sortable: true, filter: "text", flex: 2 },
  { field: "kind", headerKey: "gallery.grid.kind", filter: "text", width: 140 },
  { field: "status", headerKey: "gallery.grid.status", filter: "text", width: 120 },
  { field: "amount", headerKey: "gallery.grid.amount", sortable: true, filter: "number", width: 140, format: (row) => row.amount.toFixed(2) },
  { field: "updated_at", headerKey: "gallery.grid.updated", sortable: true, filter: "date", width: 160, format: (row) => row.updated_at.slice(0, 10) },
];

interface GridSectionProps {
  /** The grid has drawn its first rows: the page is complete and the scan may read it. */
  onReady: () => void;
}

/** The one grid of the shell over sample rows; it follows the density switch. */
export function GridSection({ onReady }: GridSectionProps) {
  const { t } = useTranslation();
  const rows = useMemo(() => sampleRows(t), [t]);
  const fetchPage = useCallback((query: string) => pageOf(rows, query), [rows]);

  return (
    <section aria-labelledby="gallery-grid">
      <Stack gap="lg">
        <Title order={2} id="gallery-grid">
          {t("gallery.sections.grid")}
        </Title>
        <Text>{t("gallery.grid.intro")}</Text>
        <div style={{ height: 520 }}>
          <DataGrid<SampleRow> columns={columns} fetchPage={fetchPage} label={t("gallery.grid.label")} onReady={onReady} />
        </div>
      </Stack>
    </section>
  );
}
