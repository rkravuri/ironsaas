import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { ProfilePage } from "./ProfilePage";

const mine = (id: string, role: string, scope: string, path: string[]) => ({
  id,
  principal_id: me().id,
  principal_name: "Admin",
  role_id: `role-${id}`,
  role_code: role.toLowerCase().replace(/ /g, "-"),
  role_name: role,
  scope_kind: scope,
  unit_id: path.length > 0 ? `unit-${id}` : null,
  record_type: null,
  record_id: null,
  unit_path: path.map((name, index) => ({ id: `unit-${id}-${index}`, name })),
  created_at: "2026-10-19T09:00:00Z",
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("My profile", () => {
  it("sits on the record template: name, login identifier, home unit and status, with details, access and sessions [ADR-0027, ADR-0033]", async () => {
    stubFetch({
      "GET /api/v1/me": () =>
        json({
          ...me(["identity.users.read"]),
          display_name: "Ada Lovelace",
          home_unit: { id: "b", name: "E2E branch", code: "e2e-branch" },
        }),
      "GET /api/v1/role-assignments/mine": () =>
        json({
          data: [
            mine("1", "Unit user reader", "unit_with_children", ["Acme", "E2E branch"]),
            mine("2", "Read only", "tenant", []),
            mine("3", "Auditor", "unit", ["Acme", "Finance"]),
          ],
        }),
    });
    renderWithProviders(<ProfilePage />);

    expect(await screen.findByRole("heading", { level: 2, name: "Ada Lovelace" })).toBeInTheDocument();
    const header = screen
      .getByRole("heading", { level: 2, name: "Ada Lovelace" })
      .closest("[data-panel=record-header]") as HTMLElement;
    expect(header).toHaveTextContent("admin@acme.localhost");
    expect(header).toHaveTextContent("Home unit: E2E branch");
    expect(within(header).getByText("Active")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Details" })).toHaveAttribute("aria-selected", "true");

    const user = userEvent.setup();
    await user.click(screen.getByRole("tab", { name: "Access" }));
    const roles = await screen.findByRole("list", { name: "Your roles" });
    expect(
      within(roles)
        .getAllByRole("listitem")
        .map((item) => item.firstChild?.textContent),
    ).toEqual([
      "Unit user reader, at Acme / E2E branch and every unit below it",
      "Read only, for the whole tenant",
      "Auditor, at Acme / Finance only",
    ]);

    await user.click(screen.getByRole("tab", { name: "Sessions" }));
    const panel = await screen.findByRole("tabpanel", { name: "Sessions" });
    for (const label of ["Started", "Ends without activity at", "Ends at the latest at"]) {
      expect(within(panel).getByText(label)).toBeInTheDocument();
    }
    expect(within(panel).getByRole("button", { name: "Sign out" })).toBeInTheDocument();
  });

  it("offers the preferences that follow the person, and says the tenant's session limits [ADR-0033, ADR-0036]", async () => {
    window.localStorage.clear();
    stubFetch({
      "GET /api/v1/me": () => json(me([])),
      "GET /api/v1/me/context": () =>
        json({
          entitlements: {
            revision: 1,
            plan: "enterprise",
            confirmed_at: "2026-10-20T09:00:00Z",
            stale: false,
            modules: [],
            features: [],
          },
          settings: { values: [{ key: "identity.session.idle_minutes", value: 30, level: "tenant" }] },
        }),
    });
    renderWithProviders(<ProfilePage />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("tab", { name: "Preferences" }));
    const preferences = await screen.findByRole("tabpanel", { name: "Preferences" });
    expect(preferences).toHaveTextContent("they follow you to every browser you sign in on");
    await user.click(within(preferences).getByRole("radio", { name: "Dark" }));
    await waitFor(() => expect(window.localStorage.getItem("iron.scheme")).toBe("dark"));
    await user.click(within(preferences).getByRole("radio", { name: "Compact" }));
    await waitFor(() => expect(document.documentElement.dataset.density).toBe("compact"));

    await user.click(screen.getByRole("tab", { name: "Sessions" }));
    const session = await screen.findByRole("tabpanel", { name: "Sessions" });
    await waitFor(() =>
      expect(session).toHaveTextContent("ends a session after 30 minutes without activity, and after 12 hours at most"),
    );
  });

  it("lets the person choose the time zone every time is shown in, kept as their own setting [TF-065 AC2, ADR-0050]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me([])),
      "PUT /api/v1/settings/shell.time_zone": () =>
        json({
          key: "shell.time_zone",
          module: "settings",
          type: "text",
          description: "The time zone the shell shows times in.",
          requirement: "",
          choices: null,
          minimum: null,
          maximum: null,
          whole_number: false,
          scopes: ["user"],
          default: "",
          value: "Europe/Berlin",
          source: { level: "user", unit: null },
          set_here: true,
        }),
    });
    renderWithProviders(<ProfilePage />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("tab", { name: "Preferences" }));
    const preferences = await screen.findByRole("tabpanel", { name: "Preferences" });
    const zone = within(preferences).getByRole("combobox", { name: "Time zone" });
    expect(zone).toHaveValue(`This browser's (${Intl.DateTimeFormat().resolvedOptions().timeZone})`);
    expect(preferences).toHaveTextContent("Every time the shell shows is in this zone, with its name.");
    await user.click(zone);
    await user.clear(zone);
    await user.type(zone, "Europe/Berl");
    // The dropdown lists the matching zones; the keys choose the first, as a person would.
    await user.keyboard("{ArrowDown}{Enter}");

    await waitFor(() => expect(calls.some((call) => call.key === "PUT /api/v1/settings/shell.time_zone")).toBe(true));
    const sent = calls.find((call) => call.key === "PUT /api/v1/settings/shell.time_zone");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({ scope: "user", unit_id: null, value: "Europe/Berlin" });
  });

  it("says what will appear when the person holds no role yet [ADR-0027]", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me([])), "GET /api/v1/role-assignments/mine": () => json({ data: [] }) });
    renderWithProviders(<ProfilePage />);
    await userEvent.setup().click(await screen.findByRole("tab", { name: "Access" }));
    await waitFor(() => expect(screen.getByText("You hold no roles yet")).toBeInTheDocument());
  });
});
