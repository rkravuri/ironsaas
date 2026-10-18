import { useCallback, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Checkbox, Code, Group, Menu, Stack, Text } from "@mantine/core";
import { IconColumns } from "@tabler/icons-react";
import { AgGridReact } from "ag-grid-react";
import {
  ColumnApiModule,
  DateFilterModule,
  InfiniteRowModelModule,
  ModuleRegistry,
  NumberFilterModule,
  PaginationModule,
  RowSelectionModule,
  TextFilterModule,
  ValidationModule,
  type ColDef,
  type GridApi,
  type GridReadyEvent,
  type IDatasource,
  type IGetRowsParams,
  type CellKeyDownEvent,
  type RowDoubleClickedEvent,
  type SelectionChangedEvent,
} from "ag-grid-community";
import { ApiError } from "@/api/client";
import type { ListMeta } from "@/api/client";
import { useDensity } from "@/design/density";
import { tokens } from "@/design/tokens";
import { buildListQuery, defaultPageSize, filtersFromGrid, maxPageSize, pageFromRows, sortFromGrid, type GridFilterModel, type SortTerm } from "./list-query";
import { NoRowsOverlay } from "./NoRowsOverlay";
import { rowKeyAction } from "./keys";
import { dataGridTheme } from "./theme";

// Only what the wrapper uses; the validation module explains a missing one in development and costs nothing in production.
ModuleRegistry.registerModules([InfiniteRowModelModule, PaginationModule, RowSelectionModule, TextFilterModule, NumberFilterModule, DateFilterModule, ColumnApiModule, ...(import.meta.env.DEV ? [ValidationModule] : [])]);

export type DataGridFilter = "text" | "number" | "date" | false;

export interface DataGridColumn<T> {
  /** The API's field name; also the sort and filter name (TF-101). */
  field: string;
  /** i18n key of the header. */
  headerKey: string;
  sortable?: boolean;
  /** The filter the API allows on the field: text for strings and enums, number and date for ordered values, false for none. */
  filter?: DataGridFilter;
  width?: number;
  flex?: number;
  hidden?: boolean;
  format?: (row: T) => string;
}

export interface ListResult<T> {
  data: T[];
  meta: ListMeta;
}

export interface DataGridProps<T> {
  columns: DataGridColumn<T>[];
  /** Fetches one page with the ListQuery string the grid built (without the question mark). */
  fetchPage: (query: string, signal?: AbortSignal) => Promise<ListResult<T>>;
  pageSize?: number;
  defaultSort?: SortTerm[];
  /** Accessible name of the grid. */
  label: string;
  /** Re-fetches everything when it changes: a filter box outside the grid, a "refresh" button. */
  refreshKey?: unknown;
  /** Called once the first rows are on screen; the gallery marks itself ready for the accessibility scan on it. */
  onReady?: () => void;
  /** The action of a row: a double click, or Ctrl+Enter (Cmd+Enter on a Mac) on the focused row. */
  onRowOpen?: (row: T) => void;
  /** The selected row, or undefined once the selection is cleared; Enter and Space select the focused row. */
  onSelectionChange?: (row: T | undefined) => void;
}

export interface DataGridProblem {
  message: string;
  correlationId: string | undefined;
}

const textFilterParams = { filterOptions: ["contains", "startsWith", "equals", "notEqual"], maxNumConditions: 1, buttons: ["apply", "reset"], debounceMs: 300 };
const numberFilterParams = { filterOptions: ["equals", "notEqual", "lessThan", "lessThanOrEqual", "greaterThan", "greaterThanOrEqual"], maxNumConditions: 1, buttons: ["apply", "reset"] };
const dateFilterParams = { filterOptions: ["equals", "lessThan", "greaterThan"], maxNumConditions: 1, buttons: ["apply", "reset"] };

/**
 * The one grid of the shell (S1 plan 3.10): AG Grid Community in the infinite row model, so paging, sorting and
 * filtering are the grid's own controls, mapped to the ListQuery grammar and answered by the standard envelope.
 * Row ids are the grid's own (by index): in this row model it asks for ids before the data is there, so a
 * data-based id would throw. Nothing else imports ag-grid (ESLint); switching to Enterprise by licence is a
 * change to this file.
 */
export function DataGridImpl<T>({ columns, fetchPage, pageSize = defaultPageSize, defaultSort = [], label, refreshKey, onReady, onRowOpen, onSelectionChange }: DataGridProps<T>) {
  const { t } = useTranslation();
  const density = useDensity();
  const apiRef = useRef<GridApi<T> | null>(null);
  const [problem, setProblem] = useState<DataGridProblem | undefined>();
  const [capped, setCapped] = useState<ListMeta | undefined>();
  const [hidden, setHidden] = useState<Set<string>>(() => new Set(columns.filter((c) => c.hidden).map((c) => c.field)));
  // The grid's block size is fixed at mount; a later change of the prop is deliberately ignored.
  const [initialPageSize] = useState(() => Math.min(pageSize, maxPageSize));

  const columnDefs = useMemo<ColDef<T>[]>(
    () =>
      columns.map((column) => {
        const def: ColDef<T> = {
          colId: column.field,
          field: column.field as never,
          headerName: t(column.headerKey),
          sortable: column.sortable ?? false,
          filter: column.filter === "text" ? "agTextColumnFilter" : column.filter === "number" ? "agNumberColumnFilter" : column.filter === "date" ? "agDateColumnFilter" : false,
          filterParams: column.filter === "text" ? textFilterParams : column.filter === "number" ? numberFilterParams : column.filter === "date" ? dateFilterParams : undefined,
          hide: hidden.has(column.field),
          resizable: true,
        };
        if (column.width !== undefined) {
          def.width = column.width;
        }
        if (column.flex !== undefined) {
          def.flex = column.flex;
        }
        if (column.format) {
          const format = column.format;
          def.valueFormatter = (params) => (params.data ? format(params.data) : "");
        }
        return def;
      }),
    [columns, hidden, t],
  );

  const datasource = useMemo<IDatasource>(
    () => ({
      getRows: (params: IGetRowsParams) => {
        const { pageNumber, pageSize: size } = pageFromRows(params.startRow, params.endRow);
        const sort = sortFromGrid(params.sortModel);
        const query = buildListQuery({
          pageNumber,
          pageSize: size,
          sort: sort.length > 0 ? sort : defaultSort,
          filters: filtersFromGrid(params.filterModel as Record<string, GridFilterModel>),
        });
        fetchPage(query).then(
          (result) => {
            setProblem(undefined);
            setCapped(result.meta.page_size_capped ? result.meta : undefined);
            params.successCallback(result.data, result.meta.total_count);
          },
          (error: unknown) => {
            setProblem(
              error instanceof ApiError
                ? { message: error.message, correlationId: error.correlationId }
                : { message: error instanceof Error ? error.message : t("errors.unexpected"), correlationId: undefined },
            );
            params.failCallback();
          },
        );
      },
    }),
    // refreshKey is in the list on purpose: a new datasource makes the grid fetch again.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [fetchPage, defaultSort, t, refreshKey],
  );

  const onGridReady = useCallback((event: GridReadyEvent<T>) => {
    apiRef.current = event.api;
  }, []);

  const onCellKeyDown = useCallback(
    (event: CellKeyDownEvent<T>) => {
      const keyboard = event.event;
      if (!(keyboard instanceof KeyboardEvent) || event.data === undefined) {
        return;
      }
      const action = rowKeyAction(keyboard);
      if (action === "open") {
        keyboard.preventDefault();
        onRowOpen?.(event.data);
      } else if (action === "select") {
        keyboard.preventDefault();
        event.node.setSelected(true, true);
      }
    },
    [onRowOpen],
  );

  const onRowDoubleClicked = useCallback(
    (event: RowDoubleClickedEvent<T>) => {
      if (event.data !== undefined) {
        onRowOpen?.(event.data);
      }
    },
    [onRowOpen],
  );

  const onSelectionChanged = useCallback(
    (event: SelectionChangedEvent<T>) => {
      onSelectionChange?.(event.api.getSelectedRows()[0]);
    },
    [onSelectionChange],
  );

  const retry = useCallback(() => {
    setProblem(undefined);
    apiRef.current?.purgeInfiniteCache();
  }, []);

  const toggleColumn = useCallback((field: string, visible: boolean) => {
    setHidden((current) => {
      const next = new Set(current);
      if (visible) {
        next.delete(field);
      } else {
        next.add(field);
      }
      return next;
    });
    apiRef.current?.setColumnsVisible([field], visible);
  }, []);

  return (
    <Stack gap="xs" style={{ height: "100%", minHeight: 420 }}>
      <Group justify="flex-end">
        <Menu closeOnItemClick={false} position="bottom-end" withinPortal>
          <Menu.Target>
            <Button variant="subtle" size="xs" leftSection={<IconColumns size={16} />}>
              {t("grid.columns")}
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            {columns.map((column) => (
              <Menu.Item key={column.field} component="div">
                <Checkbox label={t(column.headerKey)} checked={!hidden.has(column.field)} onChange={(event) => toggleColumn(column.field, event.currentTarget.checked)} size="sm" />
              </Menu.Item>
            ))}
          </Menu.Dropdown>
        </Menu>
      </Group>
      {problem && (
        <Alert color="danger" role="alert" withCloseButton onClose={() => setProblem(undefined)}>
          <Text size="sm">{problem.message}</Text>
          {problem.correlationId && (
            <Text size="xs" c="dimmed" mt={4}>
              {t("errors.correlation", { id: "" })}
              <Code>{problem.correlationId}</Code>
            </Text>
          )}
          <Button variant="light" size="xs" mt="xs" onClick={retry}>
            {t("errors.retry")}
          </Button>
        </Alert>
      )}
      {capped && (
        <Alert color="warning" role="status">
          {t("grid.capped", { size: capped.page_size, max: capped.max_page_size })}
        </Alert>
      )}
      {/* Tabular figures: numbers line up in a column. */}
      <div style={{ flex: 1, minHeight: 360, fontVariantNumeric: "tabular-nums" }} role="region" aria-label={label}>
        <AgGridReact<T>
          theme={dataGridTheme}
          columnDefs={columnDefs}
          rowModelType="infinite"
          datasource={datasource}
          cacheBlockSize={initialPageSize}
          maxBlocksInCache={2}
          maxConcurrentDatasourceRequests={1}
          infiniteInitialRowCount={1}
          pagination
          paginationPageSize={initialPageSize}
          paginationPageSizeSelector={[25, 50, 100, 200]}
          onGridReady={onGridReady}
          onFirstDataRendered={() => onReady?.()}
          rowSelection={{ mode: "singleRow", checkboxes: false, enableClickSelection: true }}
          onCellKeyDown={onCellKeyDown}
          onRowDoubleClicked={onRowDoubleClicked}
          onSelectionChanged={onSelectionChanged}
          rowHeight={tokens.density.rowHeight[density]}
          noRowsOverlayComponent={NoRowsOverlay}
          suppressCellFocus={false}
          ensureDomOrder
          animateRows={false}
          domLayout="normal"
        />
      </div>
    </Stack>
  );
}

export default DataGridImpl;
