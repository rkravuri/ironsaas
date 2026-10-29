import { afterEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { Route, Routes } from "react-router";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { SampleRecordPage } from "./SampleRecordPage";

afterEach(() => {
  vi.unstubAllGlobals();
});

const record = {
  id: "r1",
  name: "North sample",
  owning_unit_id: "u1",
  sharing_mode: "private",
  visibility_tags: ["APAC"],
  created_at: "2026-10-29T09:00:00Z",
  updated_at: "2026-10-29T09:00:00Z",
  version: 2,
};

const page = (
  <Routes>
    <Route path="/samples/:id" element={<SampleRecordPage />} />
  </Routes>
);

describe("a sample record's page", () => {
  it("shows the record's visibility tags under its header, on the record template [ADR-0057]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["samples.records.read"])),
      "GET /api/v1/sample-records/r1": () => json(record),
      "GET /api/v1/record-tags/sample_record/r1": () =>
        json({
          record_type: "sample_record",
          record_id: "r1",
          tags: [{ code: "APAC", name: "Asia Pacific", active: true }],
          may_change: false,
          choices: [],
        }),
    });
    renderWithProviders(page, { route: "/samples/r1" });

    expect(await screen.findByRole("heading", { name: "North sample" })).toBeInTheDocument();
    expect(await screen.findByRole("list", { name: "Visibility tags" })).toHaveTextContent("Asia Pacific (APAC)");
    expect(screen.getByRole("tab", { name: "Sharing" })).toBeInTheDocument();
  });

  it("answers a record outside the reader's units or tags as one that does not exist [TF-241 AC2, ADR-0032]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["samples.records.read"])),
    });
    renderWithProviders(page, { route: "/samples/r1" });

    expect(await screen.findByText("No such record")).toBeInTheDocument();
    expect(screen.getByText("There is no sample record with this id that you may see.")).toBeInTheDocument();
  });
});
