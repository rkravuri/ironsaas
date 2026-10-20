import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { render } from "@testing-library/react";
import { ApiError } from "@/api/client";
import { DataGridImpl as DataGrid, type ListResult } from "./DataGrid";

interface Row {
  id: string;
  name: string;
}

const meta = (overrides: Partial<ListResult<Row>["meta"]> = {}) => ({
  page_number: 1,
  page_size: 50,
  total_count: 1,
  total_pages: 1,
  has_previous: false,
  has_next: false,
  page_size_capped: false,
  max_page_size: 200,
  ...overrides,
});

afterEach(() => {
  vi.restoreAllMocks();
});

/** The grid itself is AG Grid's; these tests cover what the wrapper adds: the query it asks for, and how it shows problems and the cap. */
describe("the DataGrid wrapper", () => {
  it("asks the API for the first page with the default sort", async () => {
    const fetchPage = vi.fn((query: string): Promise<ListResult<Row>> => {
      void query;
      return Promise.resolve({ data: [{ id: "1", name: "one" }], meta: meta() });
    });

    render(
      <MantineProvider>
        <DataGrid<Row>
          columns={[{ field: "name", headerKey: "users.displayName", sortable: true, filter: "text" }]}
          fetchPage={fetchPage}
          defaultSort={[{ field: "name", direction: "asc" }]}
          label="rows"
        />
      </MantineProvider>,
    );

    await waitFor(() => expect(fetchPage).toHaveBeenCalled());
    const query = new URLSearchParams(fetchPage.mock.calls[0]?.[0] ?? "");
    expect(query.get("page_number")).toBe("1");
    expect(query.get("page_size")).toBe("50");
    expect(query.get("sort")).toBe("name");
  });

  it("shows the API's problem with its correlation id when a page fails", async () => {
    const fetchPage = vi.fn((): Promise<ListResult<Row>> =>
      Promise.reject(new ApiError(400, { title: "Bad Request", detail: "'nope' is not a filterable field.", error_code: "list.unknown_field" }, "corr-grid")),
    );

    render(
      <MantineProvider>
        <DataGrid<Row> columns={[{ field: "name", headerKey: "users.displayName" }]} fetchPage={fetchPage} label="rows" />
      </MantineProvider>,
    );

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("'nope' is not a filterable field.");
    expect(alert).toHaveTextContent("corr-grid");
  });

  it("tells the user when the API capped the page size", async () => {
    const fetchPage = vi.fn((): Promise<ListResult<Row>> => Promise.resolve({ data: [], meta: meta({ page_size: 200, page_size_capped: true, total_count: 0 }) }));

    render(
      <MantineProvider>
        <DataGrid<Row> columns={[{ field: "name", headerKey: "users.displayName" }]} fetchPage={fetchPage} label="rows" />
      </MantineProvider>,
    );

    const status = await screen.findByRole("status");
    expect(status).toHaveTextContent("200");
  });
});
