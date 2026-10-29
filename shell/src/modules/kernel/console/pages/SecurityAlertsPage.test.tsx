import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fill, json, me, renderWithProviders, stubFetch } from "@/test/render";
import { SecurityAlertsPage } from "./SecurityAlertsPage";

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

const link =
  "/audit?event_type=authz.permission_denied&actor_id=0198f0c2-0000-7000-8000-000000000001&since=2026-10-29T10%3A00%3A00.000000Z&until=2026-10-29T10%3A08%3A15.000000Z";

const alert = {
  id: "0198f0c2-0000-7000-8000-0000000000a1",
  tenant: "acme",
  origin: "local",
  rule: "permission_denials",
  title: "Many permission denials for one principal",
  kind: "threshold",
  severity: "high",
  state: "open",
  group_kind: "principal",
  group_value: "0198f0c2-0000-7000-8000-000000000001",
  group_name: "Paul Probe",
  count: 112,
  threshold: 100,
  window_minutes: 10,
  first_event_at: "2026-10-29T10:00:00Z",
  last_event_at: "2026-10-29T10:08:15Z",
  raised_at: "2026-10-29T10:09:00Z",
  reason: null,
  link,
  search: "/api/v1/audit-events?filter=event_type:eq:authz.permission_denied",
  runbook: "docs/security/incident-response.md#permission-denials",
  acknowledged_at: null,
  acknowledged_by: null,
  resolved_at: null,
  resolved_by: null,
  note: null,
};

const rules = { data: [], evaluated_at: "2026-10-29T10:09:00Z" };

const handlers = (permissions: string[], extra: Record<string, (init: RequestInit | undefined) => Response> = {}) => ({
  "GET /api/v1/me": () => json(me(permissions)),
  "GET /api/v1/alert-rules": () => json(rules),
  "GET /api/v1/alerts?page_number=1&page_size=50&sort=-raised_at&filter=state%3Aeq%3Aopen": () => json(emptyList),
  [`GET /api/v1/alerts/${alert.id}`]: () => json(alert),
  ...extra,
});

describe("the console's security alerts page", () => {
  it("lists the open alerts first and opens the alert a link names, with its count, the link to its events and its runbook [TF-079 AC1, ADR-0056]", async () => {
    const { calls } = stubFetch(handlers(["monitoring.alerts.read"]));
    renderWithProviders(<SecurityAlertsPage />, { route: `/console/security-alerts?alert=${alert.id}` });

    expect(await screen.findByRole("heading", { level: 2, name: "Security alerts" })).toBeInTheDocument();
    await waitFor(() =>
      expect(
        calls.some((call) => call.key.startsWith("GET /api/v1/alerts?") && call.key.includes("state%3Aeq%3Aopen")),
      ).toBe(true),
    );
    const detail = await screen.findByRole("region", { name: alert.title });
    expect(detail).toHaveTextContent("112 counted, threshold 100, within 10 minutes");
    expect(detail).toHaveTextContent("Paul Probe");
    expect(within(detail).getByText("High")).toBeInTheDocument();
    expect(within(detail).getByRole("link", { name: "Open the events in the audit trail" })).toHaveAttribute(
      "href",
      link,
    );
    expect(detail).toHaveTextContent("docs/security/incident-response.md#permission-denials");
    // Reading is not acting: without monitoring.alerts.manage there is nothing to press.
    expect(within(detail).queryByRole("button", { name: "Acknowledge" })).not.toBeInTheDocument();
  });

  it("acknowledges an alert, then resolves it only with a note, each answered by the API [ADR-0056]", async () => {
    const { calls } = stubFetch(
      handlers(["monitoring.alerts.read", "monitoring.alerts.manage"], {
        [`POST /api/v1/alerts/${alert.id}/acknowledge`]: () =>
          json({
            ...alert,
            state: "acknowledged",
            acknowledged_at: "2026-10-29T10:10:00Z",
            acknowledged_by: "Ada Admin",
          }),
        [`POST /api/v1/alerts/${alert.id}/resolve`]: () =>
          json({
            ...alert,
            state: "resolved",
            resolved_at: "2026-10-29T10:20:00Z",
            resolved_by: "Ada Admin",
            note: "A test of the import.",
          }),
      }),
    );
    renderWithProviders(<SecurityAlertsPage />, { route: `/console/security-alerts?alert=${alert.id}` });
    const user = userEvent.setup();

    const detail = await screen.findByRole("region", { name: alert.title });
    await user.click(within(detail).getByRole("button", { name: "Acknowledge" }));
    const confirm = await screen.findByRole("dialog", { name: "Acknowledge the alert?" });
    await user.click(within(confirm).getByRole("button", { name: "Acknowledge" }));
    expect(await screen.findByText(`You acknowledged "${alert.title}".`)).toBeInTheDocument();
    expect(await screen.findByText(/Acknowledged by Ada Admin/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Resolve" }));
    const dialog = await screen.findByRole("dialog", { name: "Resolve the alert?" });
    await user.click(within(dialog).getByRole("button", { name: "Resolve" }));
    expect(calls.some((call) => call.key === `POST /api/v1/alerts/${alert.id}/resolve`)).toBe(false);
    await fill(user, within(dialog).getByLabelText(/^Note/), "A test of the import.");
    await user.click(within(dialog).getByRole("button", { name: "Resolve" }));

    await waitFor(() =>
      expect(calls.some((call) => call.key === `POST /api/v1/alerts/${alert.id}/resolve`)).toBe(true),
    );
    const resolve = calls.find((call) => call.key === `POST /api/v1/alerts/${alert.id}/resolve`);
    expect(JSON.parse(resolve?.init?.body as string)).toEqual({ note: "A test of the import." });
    expect(await screen.findByText(/Resolved by Ada Admin/)).toBeInTheDocument();
  });

  it("names a failing rule's alert for what it is and leads to the rule, not to events [TF-079 AC4, ADR-0056]", async () => {
    const failing = {
      ...alert,
      kind: "failing",
      rule: "permission_denials",
      title: 'The rule "Many permission denials for one principal" is failing',
      group_kind: "rule",
      group_value: "permission_denials",
      group_name: null,
      count: 0,
      reason: "Monitoring:Rules:permission_denials:Threshold is 'a hundred', not a whole number from 1 to 100000.",
      link: "/console/alert-rules?rule=permission_denials",
      runbook: "docs/security/incident-response.md#a-failing-rule",
    };
    stubFetch(handlers(["monitoring.alerts.read"], { [`GET /api/v1/alerts/${alert.id}`]: () => json(failing) }));
    renderWithProviders(<SecurityAlertsPage />, { route: `/console/security-alerts?alert=${alert.id}` });

    const detail = await screen.findByRole("region", { name: failing.title });
    expect(detail).toHaveTextContent("is 'a hundred', not a whole number");
    expect(within(detail).getByRole("link", { name: "Open the rule" })).toHaveAttribute(
      "href",
      "/console/alert-rules?rule=permission_denials",
    );
  });
});
