import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { PasswordExpiryBanner } from "@/shell/PasswordExpiryBanner";
import { SecurityTab } from "./SecurityTab";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

const policy = {
  minimum_length: 12,
  maximum_length: 1024,
  common_check: true,
  history_depth: 10,
  maximum_age_days: 0,
  warning_days: 14,
};

const list = <T,>(data: T[]) => ({
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
  links: { self: "", first: "", previous: null, next: null, last: "" },
});

const event = (id: string, kind: string, actor: string, actorName: string | null, reason: string | null = null) => ({
  id,
  kind,
  occurred_at: `2026-10-21T0${id}:00:00Z`,
  actor,
  actor_name: actorName,
  reason,
});

const change = async (current: string, next: string, keepOthers = false) => {
  const user = userEvent.setup();
  await fill(user, await screen.findByLabelText(/^current password/i), current);
  await fill(user, screen.getByLabelText(/^new password/i), next);
  await fill(user, screen.getByLabelText(/^repeat the new password/i), next);
  if (keepOthers) {
    await user.click(screen.getByRole("checkbox", { name: /end my other sessions/i }));
  }
  await user.click(screen.getByRole("button", { name: "Change the password" }));
};

describe("My profile's Security tab", () => {
  it("changes the password with the current one and ends the other sessions unless the box is cleared [TF-017 AC3, ADR-0046]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /auth/password-policy": () => json(policy),
      "GET /api/v1/me/security-events?page_size=50": () => json(list([])),
      "POST /api/v1/me/password": () => new Response(null, { status: 204 }),
    });
    renderWithProviders(<SecurityTab />);

    expect(await screen.findByRole("checkbox", { name: /end my other sessions/i })).toBeChecked();
    await change("the old passphrase", "a brand new passphrase");
    expect(
      await screen.findByText("Your password is changed, and your other sessions have ended."),
    ).toBeInTheDocument();
    await change("a brand new passphrase", "and another passphrase", true);
    expect(await screen.findByText("Your password is changed.")).toBeInTheDocument();

    const bodies = calls
      .filter((call) => call.key === "POST /api/v1/me/password")
      .map((call) => JSON.parse(call.init?.body as string) as unknown);
    expect(bodies).toEqual([
      { current_password: "the old passphrase", new_password: "a brand new passphrase", end_other_sessions: true },
      { current_password: "a brand new passphrase", new_password: "and another passphrase", end_other_sessions: false },
    ]);
  });

  it("puts a wrong current password on its field and a reused one on the history rule [TF-017, TF-012 AC3]", async () => {
    let answer = 0;
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /auth/password-policy": () => json(policy),
      "GET /api/v1/me/security-events?page_size=50": () => json(list([])),
      "POST /api/v1/me/password": () =>
        answer++ === 0
          ? problem(400, {
              error_code: "identity.password.current_wrong",
              errors: [{ field: "current_password", code: "wrong", message: "Type your current password." }],
            })
          : problem(400, {
              error_code: "identity.password.reused",
              errors: [{ field: "password", code: "reused", message: "Choose a password you have not used recently." }],
            }),
    });
    renderWithProviders(<SecurityTab />);

    await change("not the password", "a brand new passphrase");
    expect(await screen.findByText("Type your current password.")).toBeInTheDocument();
    await change("the old passphrase", "an earlier passphrase");

    expect(await screen.findByText("Choose a password you have not used recently.")).toBeInTheDocument();
    const reused = screen.getByText(/Not one of your last 10 passwords/).closest("[data-rule]");
    expect(reused).toHaveAttribute("data-failed", "true");
  });

  it("lists the security activity newest first, saying who did each thing and why [ADR-0046]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /auth/password-policy": () => json(policy),
      "GET /api/v1/me/security-events?page_size=50": () =>
        json(
          list([
            event("3", "password_set_by_recovery", "self", null),
            event("2", "recovery_link_issued", "operator", "Platform Operator", "Lost password, ticket 4711"),
            event("1", "login_locked", "system", null),
          ]),
        ),
    });
    renderWithProviders(<SecurityTab />);

    const table = await screen.findByRole("table", { name: "Your security activity" });
    const rows = within(table).getAllByRole("row").slice(1);
    expect(rows.map((row) => row.getAttribute("data-kind"))).toEqual([
      "password_set_by_recovery",
      "recovery_link_issued",
      "login_locked",
    ]);
    expect(rows[0]).toHaveTextContent("Password set with a recovery link");
    expect(rows[0]).toHaveTextContent("You");
    expect(rows[1]).toHaveTextContent("Platform Operator, the platform operator");
    expect(rows[1]).toHaveTextContent("Reason: Lost password, ticket 4711");
    expect(rows[2]).toHaveTextContent("Login locked after too many failed sign-ins");
    expect(rows[2]).toHaveTextContent("Automatically");
  });

  it("says what will appear when nothing has happened yet [ADR-0027]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /auth/password-policy": () => json(policy),
      "GET /api/v1/me/security-events?page_size=50": () => json(list([])),
    });
    renderWithProviders(<SecurityTab />);

    expect(await screen.findByText("Nothing yet")).toBeInTheDocument();
  });
});

describe("the password expiry warning", () => {
  const at = (days: number) => new Date(Date.now() + days * 24 * 60 * 60 * 1000).toISOString();

  it("shows in the last fourteen days before the maximum age, with the way to change it, and not before [TF-012, ADR-0046]", async () => {
    // The dates before the banner reads the time, as the API's answer comes before the page draws.
    const soon = at(3);
    const later = at(30);
    stubFetch({
      "GET /api/v1/me": () => json({ ...me(), password_expires_at: soon }),
      "GET /auth/password-policy": () => json({ ...policy, maximum_age_days: 90 }),
    });
    const { unmount } = renderWithProviders(<PasswordExpiryBanner />);

    const words = await screen.findByText(/Your password expires in 3 days/);
    const banner = words.closest("[data-password-expiry]") as HTMLElement;
    expect(banner).toHaveAttribute("role", "status");
    expect(within(banner).getByRole("link", { name: "Change it in My profile." })).toHaveAttribute("href", "/me");
    unmount();

    const { calls } = stubFetch({
      "GET /api/v1/me": () => json({ ...me(), password_expires_at: later }),
      "GET /auth/password-policy": () => json({ ...policy, maximum_age_days: 90 }),
    });
    renderWithProviders(<PasswordExpiryBanner />);
    await waitFor(() => expect(calls.some((call) => call.key === "GET /auth/password-policy")).toBe(true));
    await new Promise((settled) => setTimeout(settled, 50));
    expect(document.querySelector("[data-password-expiry]")).toBeNull();
  });
});
