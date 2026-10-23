import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { RecordHistoryButton } from "./RecordHistoryButton";
import { json, renderWithProviders, stubFetch } from "@/test/render";

afterEach(() => {
  vi.unstubAllGlobals();
});

const hold = "01a19100-0000-7000-8000-000000000001";
const page = (data: unknown[]) => ({
  data,
  meta: {
    page_number: 1,
    page_size: 200,
    total_count: data.length,
    total_pages: 1,
    has_previous: false,
    has_next: false,
    page_size_capped: false,
    max_page_size: 200,
  },
  links: { self: "x", first: "x", previous: null, next: null, last: "x" },
});

describe("a record's History from a page without the record template", () => {
  it("opens the record's history in a dialog, a save as its fields, old and new [TF-062 AC1, ADR-0051]", async () => {
    stubFetch({
      [`GET /api/v1/audit-events?filter=record_type:eq:legal_hold&filter=record_id:eq:${hold}&sort=-created_at&page_size=200`]:
        () =>
          json(
            page([
              {
                id: "e1",
                event_type: "record.changed",
                event_name: "Record changed",
                outcome: "success",
                actor_id: "u1",
                actor_name: "Ada Admin",
                source: "ui",
                target: null,
                record_type: "legal_hold",
                record_id: hold,
                reason: null,
                correlation_id: "c-1",
                details: { version: "2" },
                created_at: "2026-10-23T09:00:00Z",
                created_by: "u1",
                severity: "normal",
                changes: [{ field: "release_reason", old: null, new: "Dispute settled", treatment: "plain" }],
              },
            ]),
          ),
    });
    renderWithProviders(
      <RecordHistoryButton record={{ recordType: "legal_hold", recordId: hold }} name="A sample order" compact />,
    );
    const user = userEvent.setup();

    await user.click(screen.getByRole("button", { name: "History of A sample order" }));

    const dialog = await screen.findByRole("dialog", { name: "History: A sample order" });
    const group = await within(dialog).findByRole("table", { name: "1 field changed" });
    expect(group).toHaveTextContent(/Release reasonEmptyDispute settled/);
    expect(within(dialog).getByText("Record changed")).toBeInTheDocument();
  });

  it("is there but off while no record is chosen [ADR-0051]", () => {
    renderWithProviders(<RecordHistoryButton record={undefined} name="" />);

    expect(screen.getByRole("button", { name: "History" })).toBeDisabled();
  });
});
