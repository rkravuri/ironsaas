import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Route, Routes } from "react-router";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { UserRecordPage } from "./UserRecordPage";

afterEach(() => {
  vi.unstubAllGlobals();
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
  ...overrides,
});

const manager = ["identity.users.read", "identity.users.manage"];
const inAnHour = () => new Date(Date.now() + 60 * 60_000).toISOString();

const page = () =>
  renderWithProviders(
    <Routes>
      <Route path="/users/:id" element={<UserRecordPage />} />
    </Routes>,
    { route: "/users/u1" },
  );

const actionsOf = async () => {
  const header = (await screen.findByRole("heading", { level: 2, name: "Ada Lovelace" })).closest(
    "[data-panel=record-header]",
  ) as HTMLElement;
  return header;
};

describe("the user record's actions", () => {
  it("offer Unlock, a recovery link and Deactivate only where /me/access allows managing people at their unit [ADR-0031, ADR-0046]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me([], manager)),
      "GET /api/v1/users/u1": () => json(person({ locked_until: inAnHour() })),
      "GET /api/v1/me/access?unit=sales": () => json({ scope: "unit:sales", permissions: manager }),
    });
    page();

    const header = await actionsOf();
    await waitFor(() => expect(within(header).getByRole("button", { name: "Unlock" })).toBeInTheDocument());
    expect(within(header).getByRole("button", { name: "Issue a recovery link" })).toBeInTheDocument();
    expect(within(header).getByRole("button", { name: "Deactivate" })).toBeInTheDocument();
    expect(within(header).queryByRole("button", { name: "Reactivate" })).toBeNull();
    expect(header).toHaveTextContent("Locked");
    expect(screen.getByText("Login locked until")).toBeInTheDocument();
  });

  it("offer nothing to someone who may read the person but not manage people at their unit [ADR-0031, ADR-0046]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me([], manager)),
      "GET /api/v1/users/u1": () => json(person()),
      "GET /api/v1/me/access?unit=sales": () => json({ scope: "unit:sales", permissions: ["identity.users.read"] }),
    });
    page();

    const header = await actionsOf();
    await waitFor(() => expect(calls.some((call) => call.key === "GET /api/v1/me/access?unit=sales")).toBe(true));
    await new Promise((settled) => setTimeout(settled, 50));
    expect(within(header).queryAllByRole("button")).toHaveLength(0);
  });

  it("issue a recovery link behind a confirmation and show it once with a copy button [TF-017 AC1, ADR-0046]", async () => {
    const expires = inAnHour();
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me([], manager)),
      "GET /api/v1/users/u1": () => json(person()),
      "GET /api/v1/me/access?unit=sales": () => json({ scope: "unit:sales", permissions: manager }),
      "POST /api/v1/users/u1/recovery-link": () => json({ recovery_link: "/recovery/the-code", expires_at: expires }),
    });
    page();

    fireEvent.click(await screen.findByRole("button", { name: "Issue a recovery link" }));
    const confirm = await screen.findByRole("dialog", { name: "Issue a recovery link for Ada Lovelace?" });
    expect(confirm).toHaveTextContent("once, within 15 minutes");
    expect(calls.some((call) => call.key === "POST /api/v1/users/u1/recovery-link")).toBe(false);
    fireEvent.click(within(confirm).getByRole("button", { name: "Issue the link" }));

    const shown = await screen.findByRole("dialog", { name: "A recovery link for Ada Lovelace" });
    expect(within(shown).getByLabelText("Recovery link")).toHaveValue(`${window.location.origin}/recovery/the-code`);
    expect(within(shown).getByRole("button", { name: "Copy the link" })).toBeInTheDocument();
    expect(shown).toHaveTextContent("is shown only now");
  });

  it("deactivate behind a confirmation, and say the API's reason when the last administrator must stay [ADR-0046]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me([], manager)),
      "GET /api/v1/users/u1": () => json(person()),
      "GET /api/v1/me/access?unit=sales": () => json({ scope: "unit:sales", permissions: manager }),
      "POST /api/v1/users/u1/deactivate": () =>
        problem(
          409,
          {
            error_code: "identity.user.last_administrator",
            detail:
              "Ada Lovelace is the last active tenant administrator. Make someone else tenant administrator first, then deactivate them.",
          },
          "corr-last",
        ),
    });
    page();

    fireEvent.click(await screen.findByRole("button", { name: "Deactivate" }));
    const confirm = await screen.findByRole("dialog", { name: "Deactivate Ada Lovelace?" });
    expect(confirm).toHaveTextContent("every session ends now, their access tokens are refused");
    fireEvent.click(within(confirm).getByRole("button", { name: "Deactivate" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("the last active tenant administrator");
    expect(alert).toHaveTextContent("corr-last");
  });

  it("reactivate a deactivated person, and show the new invitation of one who never set a password [ADR-0046]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me([], manager)),
      "GET /api/v1/users/u1": () => json(person({ status: "disabled" })),
      "GET /api/v1/me/access?unit=sales": () => json({ scope: "unit:sales", permissions: manager }),
      "POST /api/v1/users/u1/reactivate": () =>
        json({
          user: { ...person({ status: "invited" }), home_unit_id: "sales", home_unit: undefined },
          invitation_link: "/invitations/new-one",
          invitation_expires_at: inAnHour(),
        }),
    });
    page();

    const header = await actionsOf();
    expect(header).toHaveTextContent("Deactivated");
    await waitFor(() => expect(within(header).getByRole("button", { name: "Reactivate" })).toBeInTheDocument());
    expect(within(header).queryByRole("button", { name: "Deactivate" })).toBeNull();
    expect(within(header).queryByRole("button", { name: "Issue a recovery link" })).toBeNull();
    fireEvent.click(within(header).getByRole("button", { name: "Reactivate" }));
    fireEvent.click(
      within(await screen.findByRole("dialog", { name: "Reactivate Ada Lovelace?" })).getByRole("button", {
        name: "Reactivate",
      }),
    );

    const shown = await screen.findByRole("dialog", { name: "Ada Lovelace is reactivated" });
    expect(within(shown).getByLabelText("Invitation link")).toHaveValue(
      `${window.location.origin}/invitations/new-one`,
    );
    expect(calls.some((call) => call.key === "POST /api/v1/users/u1/reactivate")).toBe(true);
  });
});
