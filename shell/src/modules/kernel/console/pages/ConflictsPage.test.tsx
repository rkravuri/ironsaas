import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { ConflictsPage } from "./ConflictsPage";

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

const rule = {
  id: "c1",
  name: "Payments apart",
  first: {
    kind: "permission",
    value: "payments.raise",
    name: "Raise a payment",
  },
  second: { kind: "role", value: "r1", name: "Payment approver" },
  mode: "block",
  reason: "One person should not raise and approve a payment.",
  owner_id: "o1",
  owner_name: "Olga Owner",
  updated_at: "2026-10-20T08:00:00Z",
  version: 1,
};

const run = {
  id: "run1",
  trigger: "on_demand",
  rules: 2,
  conflicts: 1,
  computed_at: "2026-10-20T08:30:00Z",
};
const rules = "GET /api/v1/conflict-rules?page_number=1&page_size=50&sort=name";
const latest = "GET /api/v1/conflict-report/runs/latest";

/** The page renders a grid, the heaviest thing in the shell, so these tests wait longer than the default. */
const heavy = { timeout: 10_000 };
const heavyTest = 20_000;

function show(permissions: string[]) {
  const stub = stubFetch({
    "GET /api/v1/me": () => json(me(permissions)),
    [rules]: () => json(list([rule])),
    [latest]: () => json({ run }),
    "GET /api/v1/conflict-report?page_number=1&page_size=50&sort=principal_name": () => json(list([])),
    "POST /api/v1/conflict-report/runs": () => json({ ...run, id: "run2" }, 201),
    "GET /api/v1/permissions": () =>
      json({
        data: [
          {
            name: "payments.raise",
            module: "payments",
            description: "Raise a payment",
            risk: "medium",
          },
          {
            name: "payments.approve",
            module: "payments",
            description: "Approve a payment",
            risk: "high",
          },
        ],
      }),
    "GET /api/v1/roles?page_size=200&sort=name": () =>
      json(
        list([
          {
            id: "r1",
            code: "payment-approver",
            name: "Payment approver",
            description: "",
            is_system: false,
            principal_type: "user",
            permissions: ["payments.approve"],
            created_at: "",
            updated_at: "",
            version: 1,
          },
        ]),
      ),
    "POST /api/v1/conflict-rules": () => json({ ...rule, id: "c2" }, 201),
  });
  renderWithProviders(<ConflictsPage />, { route: "/console/conflicts" });
  return stub;
}

/** The select fields with this label, in order: each one's list carries the same label. */
const fields = (scope: HTMLElement, label: string) =>
  within(scope)
    .getAllByLabelText(label)
    .filter((element) => element.tagName === "INPUT");

/** An option of the list this field opens: both sides offer the same options. */
const optionOf = async (input: HTMLElement, name: string) => {
  await waitFor(() => expect(input.getAttribute("aria-controls")).toBeTruthy());
  return within(document.getElementById(input.getAttribute("aria-controls")!)!).findByRole("option", {
    name,
    hidden: true,
  });
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the conflict rules page (TF-036, ADR-0039)", () => {
  it(
    "lists the rules with Add, and Change and Remove waiting for a rule to be chosen [ADR-0039]",
    async () => {
      const { calls } = show(["authz.sod.read", "authz.sod.manage"]);

      expect(await screen.findByRole("heading", { name: "Conflict rules" }, heavy)).toBeInTheDocument();
      expect(await screen.findByRole("button", { name: "Add a rule" }, heavy)).toBeEnabled();
      expect(screen.getByRole("button", { name: "Change" })).toBeDisabled();
      expect(screen.getByRole("button", { name: "Remove" })).toBeDisabled();
      await waitFor(() => expect(calls.map((call) => call.key)).toContain(rules));
    },
    heavyTest,
  );

  it(
    "says when the report last ran and how, in words, and runs it on demand [ADR-0039]",
    async () => {
      const { calls } = show(["authz.sod.read", "authz.sod.manage"]);

      fireEvent.click(await screen.findByRole("tab", { name: "Report" }, heavy));
      expect(await screen.findByText(/^Last run .* \(run by hand\): 1 conflict across 2 rules\.$/)).toBeInTheDocument();
      fireEvent.click(screen.getByRole("button", { name: "Run the report now" }));

      await waitFor(() => expect(calls.some((call) => call.key === "POST /api/v1/conflict-report/runs")).toBe(true));
      // The run's answer is read again with the report, so the line says the new run.
      await waitFor(() => expect(calls.filter((call) => call.key === latest).length).toBeGreaterThan(1));
    },
    heavyTest,
  );

  it(
    "adds a rule between a permission and a role that warns, with its reason [ADR-0039]",
    async () => {
      const { calls } = show(["authz.sod.read", "authz.sod.manage"]);

      fireEvent.click(await screen.findByRole("button", { name: "Add a rule" }, heavy));
      const dialog = await screen.findByRole("dialog", {
        name: "Add a conflict rule",
      });
      fireEvent.change(within(dialog).getByLabelText("Name"), {
        target: { value: "Raise and approve" },
      });

      // Both sides start as a permission: the first field is the first side's.
      const permission = fields(dialog, "Permission")[0]!;
      fireEvent.click(permission);
      fireEvent.click(await optionOf(permission, "Raise a payment (payments.raise)"));

      const second = within(dialog).getByRole("radiogroup", {
        name: "The second side",
      });
      fireEvent.click(within(second).getByLabelText("A role"));
      const role = fields(dialog, "Role")[0]!;
      fireEvent.click(role);
      fireEvent.click(await optionOf(role, "Payment approver"));

      fireEvent.click(within(dialog).getByLabelText("Warns"));
      fireEvent.change(within(dialog).getByLabelText("Why the rule exists"), {
        target: { value: "One person should not raise and approve a payment." },
      });
      fireEvent.click(within(dialog).getByRole("button", { name: "Add the rule" }));

      await waitFor(() => expect(calls.some((call) => call.key === "POST /api/v1/conflict-rules")).toBe(true));
      const posted = calls.find((call) => call.key === "POST /api/v1/conflict-rules");
      expect(JSON.parse(posted?.init?.body as string)).toEqual({
        name: "Raise and approve",
        first: { kind: "permission", value: "payments.raise" },
        second: { kind: "role", value: "r1" },
        mode: "warn",
        reason: "One person should not raise and approve a payment.",
      });
    },
    heavyTest,
  );

  it(
    "offers a reader without authz.sod.manage the rules and the report, and nothing that changes them [ADR-0039]",
    async () => {
      show(["authz.sod.read"]);

      expect(await screen.findByRole("heading", { name: "Conflict rules" }, heavy)).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Add a rule" })).not.toBeInTheDocument();
      fireEvent.click(screen.getByRole("tab", { name: "Report" }));
      expect(await screen.findByText(/^Last run /)).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Run the report now" })).not.toBeInTheDocument();
    },
    heavyTest,
  );
});
