import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setTimeZone } from "@/i18n/dates";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { AuditPage } from "./AuditPage";

afterEach(() => {
  vi.unstubAllGlobals();
  setTimeZone(undefined);
});

const emptyList = {
  data: [],
  meta: {
    page_number: 1,
    page_size: 50,
    total_count: 0,
    total_pages: 1,
    has_previous: false,
    has_next: false,
    page_size_capped: false,
    max_page_size: 200,
  },
  links: { self: "x", first: "x", previous: null, next: null, last: "x" },
};

const first = "GET /api/v1/audit-events?page_number=1&page_size=50&sort=-created_at";
const filters = [
  "filter=event_type%3Aeq%3Aidentity.user.invited",
  "filter=created_at%3Age%3A2026-10-21T18%3A30%3A00.000Z",
  "filter=created_at%3Alt%3A2026-10-22T18%3A30%3A00.000Z",
].join("&");

const archived = (position: number, at: string) => ({
  id: `01a14e12-0000-7000-8000-00000000000${position}`,
  event_type: "identity.user.invited",
  outcome: "success",
  actor_id: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
  actor_name: "Ada Admin",
  source: "ui",
  target: "Grace Hopper",
  record_type: "principal",
  record_id: "01a14e12-0000-7000-8000-000000000099",
  reason: null,
  correlation_id: "c-1",
  details: null,
  created_at: at,
  created_by: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
  severity: "normal",
  chain_position: position,
  archived: true,
});

describe("the audit trail's search", () => {
  it("searches by type and by days in the person's zone, and exports that same search to CSV [TF-064 AC1, TF-065 AC2, ADR-0050]", async () => {
    setTimeZone("Asia/Kolkata");
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["audit.events.read"])),
      [first]: () => json(emptyList),
      [`${first}&${filters}`]: () => json(emptyList),
      [`POST /api/v1/audit-events/exports?${filters}&sort=-created_at`]: () =>
        json(
          {
            id: "01a14e12-0000-7000-8000-0000000000e1",
            rows: 12,
            created_at: "2026-10-23T09:00:00Z",
            download_url: "/api/v1/audit-events/exports/01a14e12-0000-7000-8000-0000000000e1/content",
          },
          201,
        ),
    });
    // The download is a link the page clicks; jsdom does not navigate.
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
    renderWithProviders(<AuditPage />, { route: "/audit" });
    const user = userEvent.setup();

    await waitFor(() => expect(calls.some((call) => call.key === first)).toBe(true));
    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "identity.user.invited" } });
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-10-22" } });
    fireEvent.change(screen.getByLabelText("To"), { target: { value: "2026-10-22" } });
    await user.click(screen.getByRole("button", { name: "Search" }));
    await waitFor(() => expect(calls.some((call) => call.key === `${first}&${filters}`)).toBe(true));

    await user.click(screen.getByRole("button", { name: "Export to CSV" }));
    expect(await screen.findByText(/The export holds 12 events/)).toBeInTheDocument();
    expect(click).toHaveBeenCalled();
  });

  it("finds archived events by date range, each marked archived and its time shown in the person's zone [TF-064 AC2, TF-065 AC2, ADR-0050]", async () => {
    setTimeZone("America/New_York");
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["audit.events.read"])),
      [first]: () => json(emptyList),
      "GET /api/v1/audit-events/archived?from=2025-01-06T05%3A00%3A00.000Z&to=2025-01-08T04%3A59%3A59.999Z": () =>
        json({
          data: [archived(1, "2025-01-06T14:00:00Z"), archived(2, "2025-01-07T09:30:00Z")],
          batches_read: 1,
          truncated: false,
        }),
    });
    renderWithProviders(<AuditPage />, { route: "/audit" });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("tab", { name: "Archive" }));
    const search = screen.getByRole("button", { name: "Search the archive" });
    expect(search).toBeDisabled();
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2025-01-06" } });
    fireEvent.change(screen.getByLabelText("To"), { target: { value: "2025-01-07" } });
    await user.click(search);

    const table = await screen.findByRole("table", { name: /2 archived events, from 1 batches/ });
    const rows = within(table).getAllByRole("row").slice(1);
    expect(rows[0]).toHaveTextContent("Jan 6, 2025, 9:00 AM EST");
    expect(rows[0]).toHaveTextContent("Archived");
    expect(rows[1]).toHaveTextContent("Jan 7, 2025, 4:30 AM EST");
    expect(rows[1]).toHaveTextContent("Ada Admin");
    expect(calls.some((call) => call.key.startsWith("GET /api/v1/audit-events/archived?"))).toBe(true);
  });
});
