import type { TFunction } from "i18next";
import type { ListResult } from "@/components/data-grid";

export interface SampleRow {
  id: string;
  name: string;
  kind: string;
  status: string;
  amount: number;
  updated_at: string;
}

const kinds = ["order", "invoice", "delivery"] as const;
const statuses = ["open", "closed"] as const;

/** Twenty rows of business-looking data, worded through the catalogue like everything else. */
export function sampleRows(t: TFunction): SampleRow[] {
  return Array.from({ length: 20 }, (_, index) => {
    const n = index + 1;
    return {
      id: `row-${n}`,
      name: t("gallery.grid.rowName", { n }),
      kind: t(`gallery.grid.kinds.${kinds[index % kinds.length] ?? "order"}`),
      status: t(`gallery.grid.statuses.${statuses[index % statuses.length] ?? "open"}`),
      amount: n * 125.5,
      updated_at: new Date(Date.UTC(2026, 9, n, 9, 30)).toISOString(),
    };
  });
}

/** Answers the grid's ListQuery from memory, paged as the API would page it; sorting and filtering stay the API's job. */
export function pageOf(rows: SampleRow[], query: string): Promise<ListResult<SampleRow>> {
  const params = new URLSearchParams(query);
  const pageNumber = Math.max(1, Number(params.get("page_number") ?? "1"));
  const pageSize = Math.max(1, Number(params.get("page_size") ?? "25"));
  const start = (pageNumber - 1) * pageSize;
  const totalPages = Math.max(1, Math.ceil(rows.length / pageSize));
  return Promise.resolve({
    data: rows.slice(start, start + pageSize),
    meta: {
      page_number: pageNumber,
      page_size: pageSize,
      total_count: rows.length,
      total_pages: totalPages,
      has_previous: pageNumber > 1,
      has_next: pageNumber < totalPages,
      page_size_capped: false,
      max_page_size: 200,
    },
  });
}
