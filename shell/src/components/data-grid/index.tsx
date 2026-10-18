import { lazy, Suspense } from "react";
import { Center } from "@mantine/core";
import { Loading } from "@/design/illustrations";
import type { DataGridProps } from "./DataGrid";

export type { DataGridColumn, DataGridFilter, DataGridProblem, DataGridProps, ListResult } from "./DataGrid";
export type { FilterTerm, SortTerm } from "./list-query";

// AG Grid is the largest thing in the shell; it loads with the first page that shows a grid, not with the login page.
const LazyDataGrid = lazy(() => import("./DataGrid"));

export function DataGrid<T>(props: DataGridProps<T>) {
  return (
    <Suspense
      fallback={
        <Center h={200}>
          <Loading decorative size="sm" />
        </Center>
      }
    >
      <LazyDataGrid {...(props as DataGridProps<unknown>)} />
    </Suspense>
  );
}
