import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { RetentionPage } from "./RetentionPage";

afterEach(() => {
  vi.unstubAllGlobals();
});

const session = {
  code: "session",
  description: "Sign-in sessions, counted from their end",
  default_days: 365,
  minimum_days: 30,
  maximum_days: 1095,
  days: 365,
  chosen: false,
  record_types: ["session"],
};
const trail = {
  code: "audit_event",
  description: "The audit trail, counted from each event's time",
  default_days: 2555,
  minimum_days: 2555,
  maximum_days: 10950,
  days: 2555,
  chosen: false,
  record_types: ["audit_event"],
};

describe("retention", () => {
  it("lists each class with its period, the operator's limits and its records [ADR-0050]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["audit.events.read"])),
      "GET /api/v1/retention": () => json({ classes: [session, trail] }),
    });
    renderWithProviders(<RetentionPage />, { route: "/audit/retention" });

    const table = await screen.findByRole("table", { name: "Retention classes, their period and limits" });
    const rows = within(table).getAllByRole("row").slice(1);
    expect(rows[0]).toHaveTextContent("Sessions");
    expect(rows[0]).toHaveTextContent("1 year");
    expect(rows[0]).toHaveTextContent("From 30 days to 3 years");
    expect(rows[1]).toHaveTextContent("Audit trail");
    expect(rows[1]).toHaveTextContent("7 years");
    expect(screen.queryByRole("button", { name: /^Change the retention of/ })).toBeNull();
  });

  it("refuses a period below the operator's minimum in the form, and says why when the API refuses it [TF-066 AC3, ADR-0050]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["audit.events.read", "audit.retention.manage"])),
      "GET /api/v1/retention": () => json({ classes: [{ ...session, minimum_days: 30 }] }),
      "PUT /api/v1/retention/session": () =>
        problem(422, {
          title: "Below the minimum",
          detail: "The operator's minimum for session is 180 days. Choose that or more.",
          error_code: "audit.retention.below_minimum",
        }),
    });
    renderWithProviders(<RetentionPage />, { route: "/audit/retention" });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Change the retention of Sessions" }));
    const dialog = await screen.findByRole("dialog", { name: "Retention of Sessions" });
    const days = within(dialog).getByLabelText(/^Days/);
    await user.clear(days);
    await fill(user, days, "10");
    await fill(user, within(dialog).getByLabelText(/^Why it changes/), "Shorter sessions policy");
    await user.click(within(dialog).getByRole("button", { name: "Save" }));
    expect(
      await within(dialog).findByText("The operator's minimum is 30 days. Choose that or more."),
    ).toBeInTheDocument();
    expect(calls.some((call) => call.key === "PUT /api/v1/retention/session")).toBe(false);

    // The operator's limit changed since the page loaded: the API refuses, and the form says why.
    await user.clear(days);
    await fill(user, days, "90");
    await user.click(within(dialog).getByRole("button", { name: "Save" }));
    expect(
      await within(dialog).findByText(/The operator's minimum for session is 180 days/),
    ).toBeInTheDocument();
    const sent = calls.find((call) => call.key === "PUT /api/v1/retention/session");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({ days: 90, reason: "Shorter sessions policy" });
  });
});
