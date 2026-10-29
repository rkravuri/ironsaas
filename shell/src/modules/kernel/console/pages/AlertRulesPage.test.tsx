import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { AlertRulesPage } from "./AlertRulesPage";

afterEach(() => {
  vi.unstubAllGlobals();
});

const values = (threshold: number, window: number, above: number | null = null) => ({
  threshold,
  window_minutes: window,
  cooldown_minutes: 60,
  above,
  severity: "high",
});

const denials = {
  name: "permission_denials",
  title: "Many permission denials for one principal",
  event_types: ["authz.permission_denied"],
  group_by: "principal",
  at_once: false,
  runbook: "docs/security/incident-response.md#permission-denials",
  operator: values(100, 10),
  tenant: { threshold: null, window_minutes: null, above: null },
  effective: values(100, 10),
  bounds: {
    threshold_editable: true,
    threshold_minimum: 1,
    threshold_maximum: 100,
    window_minimum: 10,
    window_maximum: 1440,
    above_editable: false,
    above_minimum: null,
    above_maximum: null,
  },
  state: "running",
  reason: null,
  last_run_at: "2026-10-29T10:09:00Z",
  failing_since: null,
};

const impersonations = {
  ...denials,
  name: "impersonations",
  title: "Someone signed in as another person",
  at_once: true,
  operator: values(1, 10),
  effective: values(1, 10),
  bounds: { ...denials.bounds, threshold_editable: false, threshold_maximum: 1 },
};

const exports = {
  ...denials,
  name: "mass_exports",
  title: "An export of more than 10,000 records",
  at_once: false,
  operator: values(1, 10, 10_000),
  effective: values(1, 10, 10_000),
  state: "failing",
  reason:
    "Monitoring:Rules:mass_exports:Treshold is no value of a rule; the values are Threshold, WindowMinutes, CooldownMinutes, Above, Severity.",
  bounds: {
    ...denials.bounds,
    threshold_editable: false,
    above_editable: true,
    above_minimum: 1,
    above_maximum: 10_000,
  },
};

const rules = { data: [denials, impersonations, exports], evaluated_at: "2026-10-29T10:09:00Z" };

describe("the console's alert rules page", () => {
  it("shows each rule's values, the operator's, and whether it runs or fails, with why [TF-079 AC4, ADR-0056]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["monitoring.alerts.read"])),
      "GET /api/v1/alert-rules": () => json(rules),
    });
    renderWithProviders(<AlertRulesPage />, { route: "/console/alert-rules?rule=mass_exports" });

    const table = await screen.findByRole("table", { name: /Each rule/ });
    const row = (name: string) => table.querySelector(`[data-alert-rule="${name}"]`) as HTMLElement;
    expect(row("permission_denials")).toHaveTextContent("100 events within 10 minutes");
    expect(row("impersonations")).toHaveTextContent("Each event, at once");
    expect(row("mass_exports")).toHaveTextContent("An export of more than 10,000 records");
    expect(within(row("mass_exports")).getByText("Failing")).toBeInTheDocument();
    expect(row("mass_exports")).toHaveTextContent("Treshold is no value of a rule");
    expect(within(row("permission_denials")).getByText("Running")).toBeInTheDocument();
    // The failing rule's alert links here by name: the page says why at once.
    expect(screen.getByText(/The rule "An export of more than 10,000 records" is failing/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Change/ })).not.toBeInTheDocument();
  });

  it("makes a rule stricter within its bounds, never looser, and says what the API refused [TF-079 AC1, ADR-0056]", async () => {
    let saves = 0;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["monitoring.alerts.read", "monitoring.rules.manage"])),
      "GET /api/v1/alert-rules": () => json(rules),
      "PUT /api/v1/alert-rules/permission_denials": () =>
        ++saves === 1
          ? problem(
              400,
              {
                error_code: "monitoring.rule.looser",
                detail: "A tenant can make an alert rule stricter than its platform operator's values, never looser.",
              },
              "corr-looser",
            )
          : json({ ...denials, tenant: { threshold: 20, window_minutes: 30, above: null }, effective: values(20, 30) }),
    });
    renderWithProviders(<AlertRulesPage />, { route: "/console/alert-rules" });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: 'Change "Many permission denials for one principal"' }));
    expect(
      screen.queryByRole("button", { name: 'Change "Someone signed in as another person"' }),
    ).not.toBeInTheDocument();
    const dialog = await screen.findByRole("dialog", { name: 'Change "Many permission denials for one principal"' });
    const threshold = within(dialog).getByLabelText("Threshold");
    fireEvent.change(threshold, { target: { value: "150" } });
    await user.click(within(dialog).getByRole("button", { name: "Save" }));
    expect(await within(dialog).findByText("Choose from 1 to 100.")).toBeInTheDocument();
    expect(calls.some((call) => call.key === "PUT /api/v1/alert-rules/permission_denials")).toBe(false);

    fireEvent.change(threshold, { target: { value: "20" } });
    fireEvent.change(within(dialog).getByLabelText("Window in minutes"), { target: { value: "30" } });
    await user.click(within(dialog).getByRole("button", { name: "Save" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("never looser");
    await user.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(calls.filter((call) => call.key === "PUT /api/v1/alert-rules/permission_denials")).toHaveLength(2),
    );
    const last = calls.filter((call) => call.key === "PUT /api/v1/alert-rules/permission_denials").at(-1);
    expect(JSON.parse(last?.init?.body as string)).toEqual({ threshold: 20, window_minutes: 30, above: null });
    expect(
      await screen.findByText(
        '"Many permission denials for one principal" now runs with: 20 events within 30 minutes.',
      ),
    ).toBeInTheDocument();
  });
});
