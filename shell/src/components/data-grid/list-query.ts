/**
 * The ListQuery grammar (TF-101, ADR-0009) as the API reads it, built from what the grid reports (S1 plan 3.10):
 *   page_number=2&page_size=50&sort=-created_at,login_id&filter=status:eq:active&filter=login_id:contains:ravi
 * Pure functions, so the mapping is tested without a grid.
 */

export const maxPageSize = 200;
export const defaultPageSize = 50;

export interface SortTerm {
  field: string;
  direction: "asc" | "desc";
}

export type FilterOperator = "eq" | "ne" | "lt" | "lte" | "gt" | "gte" | "contains" | "startswith" | "in";

export interface FilterTerm {
  field: string;
  operator: FilterOperator;
  value: string;
}

export interface ListQueryParts {
  pageNumber: number;
  pageSize: number;
  sort: SortTerm[];
  filters: FilterTerm[];
}

/** AG Grid's sort model: one entry per sorted column, in order. */
export interface GridSortModelItem {
  colId: string;
  sort: "asc" | "desc" | null | undefined;
}

/** AG Grid's simple filter models: text, number and date, one condition each (the wrapper allows no more). */
export type GridFilterModel =
  | { filterType: "text"; type: string; filter?: string | null }
  | { filterType: "number"; type: string; filter?: number | null; filterTo?: number | null }
  | { filterType: "date"; type: string; dateFrom?: string | null; dateTo?: string | null };

export function sortFromGrid(model: readonly GridSortModelItem[]): SortTerm[] {
  return model
    .filter((item) => item.sort === "asc" || item.sort === "desc")
    .map((item) => ({ field: item.colId, direction: item.sort as "asc" | "desc" }));
}

const textOperators: Record<string, FilterOperator> = {
  contains: "contains",
  startsWith: "startswith",
  equals: "eq",
  notEqual: "ne",
};

const orderedOperators: Record<string, FilterOperator> = {
  equals: "eq",
  notEqual: "ne",
  lessThan: "lt",
  lessThanOrEqual: "lte",
  greaterThan: "gt",
  greaterThanOrEqual: "gte",
};

/** Grid filter models become filter terms; a model the grammar cannot express is dropped rather than guessed. */
export function filtersFromGrid(model: Readonly<Record<string, GridFilterModel>>): FilterTerm[] {
  const terms: FilterTerm[] = [];
  for (const [field, filter] of Object.entries(model)) {
    if (filter.filterType === "text") {
      const operator = textOperators[filter.type];
      if (operator && filter.filter) {
        terms.push({ field, operator, value: filter.filter });
      }
    } else if (filter.filterType === "number") {
      const operator = orderedOperators[filter.type];
      if (operator && filter.filter !== null && filter.filter !== undefined) {
        terms.push({ field, operator, value: String(filter.filter) });
      }
    } else {
      const operator = orderedOperators[filter.type];
      if (operator && filter.dateFrom) {
        // AG Grid gives "YYYY-MM-DD HH:mm:ss"; the API reads ISO 8601.
        terms.push({ field, operator, value: filter.dateFrom.replace(" ", "T") });
      }
    }
  }
  return terms;
}

export function pageFromRows(startRow: number, endRow: number): { pageNumber: number; pageSize: number } {
  const pageSize = Math.min(Math.max(endRow - startRow, 1), maxPageSize);
  return { pageNumber: Math.floor(startRow / pageSize) + 1, pageSize };
}

/** The query string, without the leading question mark. Sort and filter terms keep their order. */
export function buildListQuery(parts: ListQueryParts): string {
  const query = new URLSearchParams();
  query.set("page_number", String(parts.pageNumber));
  query.set("page_size", String(Math.min(parts.pageSize, maxPageSize)));
  if (parts.sort.length > 0) {
    query.set("sort", parts.sort.map((term) => (term.direction === "desc" ? `-${term.field}` : term.field)).join(","));
  }
  for (const term of parts.filters) {
    query.append("filter", `${term.field}:${term.operator}:${term.value}`);
  }
  return query.toString();
}
