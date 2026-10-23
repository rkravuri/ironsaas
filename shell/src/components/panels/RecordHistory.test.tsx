import { afterEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { setTimeZone } from "@/i18n/dates";
import { json, renderWithProviders, stubFetch } from "@/test/render";
import { RecordHistory } from "./RecordHistory";

afterEach(() => {
  vi.unstubAllGlobals();
  setTimeZone(undefined);
});

const record = "01a14e12-0000-7000-8000-000000000099";
const event = (id: string, type: string, at: string, extra: Record<string, unknown> = {}) => ({
  id,
  event_type: type,
  outcome: "success",
  actor_id: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
  actor_name: "Ada Admin",
  source: "ui",
  target: "Grace Hopper",
  record_type: "principal",
  record_id: record,
  reason: null,
  correlation_id: "c-1",
  details: null,
  created_at: at,
  created_by: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
  severity: "normal",
  chain_position: 7,
  ...extra,
});
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

describe("a record's History tab", () => {
  it("reads every event of the record from the audit trail, newest first, each with who and when in the person's zone [TF-064 AC1, TF-065 AC2, ADR-0050]", async () => {
    setTimeZone("Asia/Kolkata");
    const { calls } = stubFetch({
      [`GET /api/v1/audit-events?filter=record_type:eq:principal&filter=record_id:eq:${record}&sort=-created_at&page_size=200`]:
        () =>
          json(
            page([
              event("e2", "identity.user.deactivated", "2026-10-22T10:00:00Z", {
                reason: "Left the company",
                impersonator_id: "01a14e12-0000-7000-8000-0000000000f1",
                impersonator_name: "Olga Operator",
              }),
              event("e1", "identity.user.invited", "2026-10-20T09:00:00Z"),
            ]),
          ),
    });
    renderWithProviders(<RecordHistory recordType="principal" recordId={record} />);

    const items = await screen.findAllByText(/^identity\.user\./);
    expect(items.map((item) => item.textContent)).toEqual(["identity.user.deactivated", "identity.user.invited"]);
    expect(screen.getByText("Grace Hopper: Left the company")).toBeInTheDocument();
    expect(
      screen.getByText("Olga Operator signed in as Ada Admin, Oct 22, 2026, 3:30 PM GMT+5:30"),
    ).toBeInTheDocument();
    expect(screen.getByText("Ada Admin, Oct 20, 2026, 2:30 PM GMT+5:30")).toBeInTheDocument();
    expect(calls.filter((call) => call.key.includes("/audit-events"))).toHaveLength(1);
  });
});
