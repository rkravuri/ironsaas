import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { ProfilePage } from "./ProfilePage";

afterEach(() => {
  vi.unstubAllGlobals();
});

const list = (data: unknown[]) => ({
  data,
  meta: {
    page_number: 1,
    page_size: 50,
    total_count: data.length,
    total_pages: 1,
    has_previous: false,
    has_next: false,
    page_size_capped: false,
    max_page_size: 200,
  },
  links: { self: "x", first: "x", previous: null, next: null, last: "x" },
});

const row = (id: string, overrides: Record<string, unknown> = {}) => ({
  id,
  principal_id: me().id,
  login_id: "admin@acme.localhost",
  display_name: "Admin",
  browser: "Chrome",
  platform: "Windows",
  city: "London",
  country: "United Kingdom",
  method: "password",
  started_at: "2026-10-22T08:00:00Z",
  last_activity_at: "2026-10-22T09:00:00Z",
  impersonator_name: null,
  current: false,
  ...overrides,
});

const mineUrl = "GET /api/v1/me/sessions?sort=-last_activity_at&page_size=50";

describe("My profile's Sessions tab", () => {
  it("lists every session with its device, place and method, and ends another one after asking [TF-022 AC4, ADR-0049]", async () => {
    let ended = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me([])),
      [mineUrl]: () =>
        json(
          list([
            row("this", { current: true }),
            ...(ended
              ? []
              : [row("phone", { browser: "Safari", platform: "iOS", city: null, country: "Sweden", method: "sso" })]),
          ]),
        ),
      "POST /api/v1/me/sessions/phone/end": () => {
        ended = true;
        return new Response(null, { status: 204 });
      },
    });
    renderWithProviders(<ProfilePage />, { route: "/me?tab=sessions" });
    const user = userEvent.setup();

    const table = await screen.findByRole("table", { name: /Your live sessions/ });
    const rows = within(table).getAllByRole("row").slice(1);
    expect(rows[0]).toHaveTextContent("Chrome on Windows");
    expect(rows[0]).toHaveTextContent("London, United Kingdom");
    expect(rows[0]).toHaveTextContent("This one");
    expect(within(rows[0]!).queryByRole("button")).toBeNull();
    expect(rows[1]).toHaveTextContent("Safari on iOS");
    expect(rows[1]).toHaveTextContent("Sweden");
    expect(rows[1]).toHaveTextContent("Company login");
    // The places come from DB-IP, credited with a link where they show, as its licence asks.
    expect(screen.getByRole("link", { name: "IP Geolocation by DB-IP" })).toHaveAttribute("href", "https://db-ip.com");

    await user.click(within(rows[1]!).getByRole("button", { name: "End the session in Safari on iOS, Sweden" }));
    const dialog = await screen.findByRole("dialog", { name: "End this session?" });
    await user.click(within(dialog).getByRole("button", { name: "End" }));

    await waitFor(() => expect(calls.some((call) => call.key === "POST /api/v1/me/sessions/phone/end")).toBe(true));
    expect(await screen.findByRole("status")).toHaveTextContent("The session in Safari on iOS ended.");
  });

  it("signs out everywhere else in one step [TF-022 AC4]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me([])),
      [mineUrl]: () => json(list([row("this", { current: true }), row("a"), row("b")])),
      "POST /api/v1/me/sessions/end-others": () => json({ sessions_ended: 2 }),
    });
    renderWithProviders(<ProfilePage />, { route: "/me?tab=sessions" });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Sign out everywhere else" }));
    const dialog = await screen.findByRole("dialog", { name: "Sign out everywhere else?" });
    expect(dialog).toHaveTextContent("Your 2 other sessions end now.");
    await user.click(within(dialog).getByRole("button", { name: "Sign out everywhere else" }));

    await waitFor(() => expect(calls.some((call) => call.key === "POST /api/v1/me/sessions/end-others")).toBe(true));
    expect(await screen.findByRole("status")).toHaveTextContent("2 other sessions ended.");
  });

  it("offers nothing to end while someone is signed in as the person [TF-025 AC5, ADR-0049]", async () => {
    stubFetch({
      "GET /api/v1/me": () =>
        json({
          ...me([]),
          impersonation: {
            impersonator_name: "Ada Admin",
            until: "2026-10-22T12:00:00Z",
            reason: "Help",
            operator: false,
          },
        }),
      [mineUrl]: () => json(list([row("this", { current: true }), row("other")])),
    });
    renderWithProviders(<ProfilePage />, { route: "/me?tab=sessions" });

    const table = await screen.findByRole("table", { name: /Your live sessions/ });
    expect(within(table).queryAllByRole("button")).toHaveLength(0);
    expect(screen.queryByRole("button", { name: "Sign out everywhere else" })).toBeNull();
  });
});
