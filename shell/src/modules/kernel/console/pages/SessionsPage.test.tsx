import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { SessionsPage } from "./SessionsPage";

afterEach(() => {
  vi.unstubAllGlobals();
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

const summary = {
  signed_in_now: 7,
  days: [
    { date: "2026-10-21", started: 12, peak_people: 5, ended_by_administrator: 0 },
    { date: "2026-10-22", started: 9, peak_people: 6, ended_by_administrator: 1 },
  ],
};

const handlers = (extra: Record<string, (init: RequestInit | undefined) => Response> = {}) => ({
  "GET /api/v1/me": () => json(me(["identity.sessions.manage"])),
  "GET /api/v1/sessions/summary?days=14": () => json(summary),
  "GET /api/v1/sessions?page_number=1&page_size=50&sort=-last_activity_at": () => json(emptyList),
  ...extra,
});

describe("the console's sessions page", () => {
  it("shows the people signed in now and a summary of each day, newest first [TF-022 AC5, ADR-0049]", async () => {
    stubFetch(handlers());
    renderWithProviders(<SessionsPage />, { route: "/console/sessions" });

    expect(await screen.findByRole("heading", { level: 2, name: "Sessions" })).toBeInTheDocument();
    expect(await screen.findByText("People signed in now")).toBeInTheDocument();
    expect(screen.getByText("7")).toBeInTheDocument();
    const table = screen.getByRole("table", { name: /The last 14 days/ });
    const rows = within(table).getAllByRole("row").slice(1);
    expect(
      rows.map((row) =>
        Array.from(row.querySelectorAll("td"))
          .slice(1)
          .map((cell) => cell.textContent),
      ),
    ).toEqual([
      ["9", "6", "1"],
      ["12", "5", "0"],
    ]);
  });

  it("lists everyone's live sessions with nothing chosen to end yet, and says so when there are none [TF-022 AC3]", async () => {
    const { calls } = stubFetch(handlers());
    renderWithProviders(<SessionsPage />, { route: "/console/sessions" });

    expect(await screen.findByRole("button", { name: "End selected session" })).toBeDisabled();
    await waitFor(() =>
      expect(
        calls.some((call) => call.key === "GET /api/v1/sessions?page_number=1&page_size=50&sort=-last_activity_at"),
      ).toBe(true),
    );
    expect(await screen.findByText(/Nothing here yet|No rows|nothing to show/i)).toBeInTheDocument();
  });

  it("revokes everything only with a reason and the tenant's identifier typed, and says why the API refused [TF-029 AC1, ADR-0049]", async () => {
    let tries = 0;
    const { calls } = stubFetch(
      handlers({
        "POST /api/v1/sessions/revoke-tenant": () =>
          ++tries === 1
            ? problem(
                400,
                {
                  error_code: "identity.revocation.confirmation",
                  detail: "Type the tenant's identifier, localhost, to confirm.",
                },
                "corr-type",
              )
            : json({ sessions_ended: 41 }),
      }),
    );
    renderWithProviders(<SessionsPage />, { route: "/console/sessions" });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Revoke every session and token" }));
    const dialog = await screen.findByRole("dialog", { name: "Revoke every session and token?" });
    expect(dialog).toHaveTextContent("yours too");
    await user.click(within(dialog).getByRole("button", { name: "Revoke everything" }));
    expect(await within(dialog).findByText("Say why: the reason is kept in the audit trail.")).toBeInTheDocument();

    await fill(user, within(dialog).getByLabelText(/^Reason/), "Credential stuffing seen in the logs");
    fireEvent.change(within(dialog).getByLabelText(/^Type .* to confirm/), { target: { value: "wrong" } });
    await user.click(within(dialog).getByRole("button", { name: "Revoke everything" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("corr-type");
    fireEvent.change(within(dialog).getByLabelText(/^Type .* to confirm/), { target: { value: "localhost" } });
    await user.click(within(dialog).getByRole("button", { name: "Revoke everything" }));

    await waitFor(() =>
      expect(calls.filter((call) => call.key === "POST /api/v1/sessions/revoke-tenant")).toHaveLength(2),
    );
    const last = calls.filter((call) => call.key === "POST /api/v1/sessions/revoke-tenant").at(-1);
    expect(JSON.parse(last?.init?.body as string)).toEqual({
      reason: "Credential stuffing seen in the logs",
      confirmation: "localhost",
    });
  });
});
