import { describe, expect, it } from "vitest";
import { buildListQuery, filtersFromGrid, pageFromRows, sortFromGrid } from "./list-query";

describe("the ListQuery mapping", () => {
  it("builds page, sort and filters in the API's grammar", () => {
    const query = buildListQuery({
      pageNumber: 2,
      pageSize: 50,
      sort: [
        { field: "created_at", direction: "desc" },
        { field: "login_id", direction: "asc" },
      ],
      filters: [
        { field: "status", operator: "eq", value: "active" },
        { field: "login_id", operator: "contains", value: "ravi" },
      ],
    });

    expect(query).toBe(
      "page_number=2&page_size=50&sort=-created_at%2Clogin_id&filter=status%3Aeq%3Aactive&filter=login_id%3Acontains%3Aravi",
    );
    expect(new URLSearchParams(query).getAll("filter")).toEqual(["status:eq:active", "login_id:contains:ravi"]);
  });

  it("caps the page size at the API's maximum", () => {
    expect(buildListQuery({ pageNumber: 1, pageSize: 500, sort: [], filters: [] })).toBe("page_number=1&page_size=200");
    expect(pageFromRows(400, 600)).toEqual({ pageNumber: 3, pageSize: 200 });
    expect(pageFromRows(0, 50)).toEqual({ pageNumber: 1, pageSize: 50 });
    expect(pageFromRows(100, 150)).toEqual({ pageNumber: 3, pageSize: 50 });
  });

  it("maps the grid's sort model in order and drops unsorted columns", () => {
    expect(
      sortFromGrid([
        { colId: "a", sort: "desc" },
        { colId: "b", sort: null },
        { colId: "c", sort: "asc" },
      ]),
    ).toEqual([
      { field: "a", direction: "desc" },
      { field: "c", direction: "asc" },
    ]);
  });

  it("maps text, number and date filter models and drops what the grammar cannot say", () => {
    const filters = filtersFromGrid({
      login_id: { filterType: "text", type: "contains", filter: "ravi" },
      display_name: { filterType: "text", type: "startsWith", filter: "R" },
      status: { filterType: "text", type: "notEqual", filter: "disabled" },
      version: { filterType: "number", type: "greaterThanOrEqual", filter: 3 },
      created_at: { filterType: "date", type: "lessThan", dateFrom: "2026-10-01 00:00:00" },
      blank: { filterType: "text", type: "blank" },
      empty: { filterType: "text", type: "contains", filter: "" },
    });

    expect(filters).toEqual([
      { field: "login_id", operator: "contains", value: "ravi" },
      { field: "display_name", operator: "startswith", value: "R" },
      { field: "status", operator: "ne", value: "disabled" },
      { field: "version", operator: "gte", value: "3" },
      { field: "created_at", operator: "lt", value: "2026-10-01T00:00:00" },
    ]);
  });
});
