import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import { Route, Routes } from "react-router";
import userEvent from "@testing-library/user-event";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { leaveFor } from "@/auth/leave";
import { UserRecordPage } from "./UserRecordPage";

vi.mock("@/auth/leave", () => ({ leaveFor: vi.fn() }));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(leaveFor).mockReset();
});

const person = (overrides: Record<string, unknown> = {}) => ({
  id: "u1",
  login_id: "ada@acme.localhost",
  display_name: "Ada Lovelace",
  type: "user",
  status: "active",
  email: "ada@example.com",
  home_unit: { id: "sales", name: "Sales", code: "sales" },
  last_login_at: null,
  created_at: "2026-10-01T09:00:00Z",
  updated_at: "2026-10-02T09:00:00Z",
  version: 3,
  locked_until: null,
  factors: 1,
  takeover_allowed: true,
  ...overrides,
});

const all = ["identity.users.read", "identity.users.manage", "identity.impersonation.start"];

const page = () =>
  renderWithProviders(
    <Routes>
      <Route path="/users/:id" element={<UserRecordPage />} />
    </Routes>,
    { route: "/users/u1" },
  );

const header = async () =>
  (await screen.findByRole("heading", { level: 2, name: "Ada Lovelace" })).closest(
    "[data-panel=record-header]",
  ) as HTMLElement;

describe("signing in as someone, revoking, and the emergency mark on the user record", () => {
  it("signs in as the person only with a reason, then starts the whole shell again as them [TF-025 AC1, ADR-0049]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me([], all)),
      "GET /api/v1/users/u1": () => json(person()),
      "GET /api/v1/me/access?unit=sales": () => json({ scope: "unit:sales", permissions: all }),
      "POST /api/v1/users/u1/impersonation": () =>
        json({ principal_id: "u1", display_name: "Ada Lovelace", until: "2026-10-22T12:00:00Z" }),
    });
    page();
    const user = userEvent.setup();

    await user.click(await within(await header()).findByRole("button", { name: "Sign in as" }));
    const dialog = await screen.findByRole("dialog", { name: "Sign in as Ada Lovelace?" });
    expect(dialog).toHaveTextContent("Every action names you both");
    await user.click(within(dialog).getByRole("button", { name: "Sign in as them" }));
    expect(await within(dialog).findByText("Say why: the reason is kept in the audit trail.")).toBeInTheDocument();
    expect(calls.some((call) => call.key === "POST /api/v1/users/u1/impersonation")).toBe(false);

    await fill(user, within(dialog).getByLabelText(/^Reason/), "Their invoices do not show");
    await user.click(within(dialog).getByRole("button", { name: "Sign in as them" }));

    await waitFor(() => expect(leaveFor).toHaveBeenCalledWith("/"));
    const sent = calls.find((call) => call.key === "POST /api/v1/users/u1/impersonation");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({ reason: "Their invoices do not show" });
  });

  it("does not offer to sign in as someone whose permissions the reader lacks, nor to anyone without the permission [TF-025 AC4, ADR-0049]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me([], all)),
      "GET /api/v1/users/u1": () => json(person({ takeover_allowed: false })),
      "GET /api/v1/me/access?unit=sales": () => json({ scope: "unit:sales", permissions: all }),
    });
    page();

    const actions = await header();
    await waitFor(() => expect(within(actions).getByRole("button", { name: "Revoke everything" })).toBeInTheDocument());
    expect(within(actions).queryByRole("button", { name: "Sign in as" })).toBeNull();
  });

  it("revokes everything of the person with a reason and says how many sessions ended [TF-029 AC4, ADR-0049]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me([], all)),
      "GET /api/v1/users/u1": () => json(person()),
      "GET /api/v1/me/access?unit=sales": () => json({ scope: "unit:sales", permissions: all }),
      "POST /api/v1/users/u1/revoke": () => json({ sessions_ended: 3 }),
    });
    page();
    const user = userEvent.setup();

    await user.click(await within(await header()).findByRole("button", { name: "Revoke everything" }));
    const dialog = await screen.findByRole("dialog", { name: "Revoke everything of Ada Lovelace?" });
    await fill(user, within(dialog).getByLabelText(/^Reason/), "Phishing report");
    await user.click(within(dialog).getByRole("button", { name: "Revoke everything" }));

    expect(await screen.findByText(/Everything of Ada Lovelace is revoked: 3 sessions ended/)).toBeInTheDocument();
    const sent = calls.find((call) => call.key === "POST /api/v1/users/u1/revoke");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({ reason: "Phishing report" });
  });

  it("marks an emergency account, shows the mark, and says why the API refused a third [ADR-0049]", async () => {
    let marked = false;
    stubFetch({
      "GET /api/v1/me": () => json(me([], all)),
      "GET /api/v1/users/u1": () => json(person({ emergency: marked })),
      "GET /api/v1/me/access?unit=sales": () => json({ scope: "unit:sales", permissions: all }),
      "PUT /api/v1/users/u1/emergency": () => {
        marked = true;
        return json(person({ emergency: true }));
      },
    });
    page();
    const user = userEvent.setup();

    await user.click(await within(await header()).findByRole("button", { name: "Make emergency account" }));
    const dialog = await screen.findByRole("dialog", { name: "Make Ada Lovelace's account an emergency account?" });
    expect(dialog).toHaveTextContent("A tenant has two at most.");
    await user.click(within(dialog).getByRole("button", { name: "Make emergency account" }));

    expect(await within(await header()).findByText(/Emergency account/)).toBeInTheDocument();
  });

  it("explains a refusal of the emergency mark with its correlation id [ADR-0049]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me([], all)),
      "GET /api/v1/users/u1": () => json(person()),
      "GET /api/v1/me/access?unit=sales": () => json({ scope: "unit:sales", permissions: all }),
      "PUT /api/v1/users/u1/emergency": () =>
        problem(
          409,
          { error_code: "identity.emergency.limit", detail: "This tenant has its 2 emergency accounts already." },
          "corr-limit",
        ),
    });
    page();
    const user = userEvent.setup();

    await user.click(await within(await header()).findByRole("button", { name: "Make emergency account" }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Make emergency account" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("This tenant has its 2 emergency accounts already.");
    expect(alert).toHaveTextContent("corr-limit");
  });
});
