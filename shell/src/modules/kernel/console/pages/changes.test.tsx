import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { request } from "@/api/client";
import type { SettingView } from "@/api/settings";
import { HeldChangeNotice } from "@/shell/HeldChangeNotice";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { ChangeRecordPage } from "./ChangeRecordPage";
import { ChangesPage } from "./ChangesPage";
import { MyPendingChanges } from "./MyPendingChanges";
import { changeRows } from "./change-words";
import { displayValue } from "./setting-words";
import i18n from "@/i18n";

afterEach(() => {
  vi.unstubAllGlobals();
  window.sessionStorage.clear();
});

const approver = { ...me(["authz.roles.manage", "authz.roles.read"]), id: "u-approver", display_name: "Bea Approver" };
const requester = { ...me(["authz.roles.manage", "authz.roles.read"]), id: "u-requester" };
const emptyPage = {
  data: [],
  meta: {
    page_number: 1,
    page_size: 50,
    total_count: 0,
    total_pages: 0,
    has_previous: false,
    has_next: false,
    page_size_capped: false,
    max_page_size: 200,
  },
  links: { self: "x", first: "x", previous: null, next: null, last: "x" },
};
const change = {
  id: "c-1",
  area: "roles",
  kind: "authz.role.update",
  subject: "role:buyer",
  title: "Change the role Buyer",
  before: { code: "buyer", name: "Buyer", permissions: ["orders.read"] },
  after: { code: "buyer", name: "Buyer", permissions: ["orders.read", "orders.approve"] },
  reason: "Buyers approve small orders from November.",
  requested_by: "u-requester",
  requester_name: "Ravi Requester",
  requested_at: "2026-10-23T09:00:00Z",
  status: "pending",
  expires_at: "2026-10-30T09:00:00Z",
  reviews: 0,
  restarted_at: null,
  restart_note: null,
  decided_by: null,
  decider_name: null,
  decided_at: null,
  decision_reason: null,
  signature_id: null,
  permission: "authz.roles.manage",
  scope_unit_id: null,
  may_decide: true,
  signed: false,
};

const renderRecord = () =>
  renderWithProviders(
    <Routes>
      <Route path="/console/changes/:id" element={<ChangeRecordPage />} />
    </Routes>,
    { route: "/console/changes/c-1" },
  );

describe("the pending changes", () => {
  it("lists the changes to approve first, the person's own on Mine, and every one only with changes.changes.read [TF-068 AC1, ADR-0052]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(approver),
      "GET /api/v1/changes?view=to_approve&page_number=1&page_size=50": () => json({ ...emptyPage, data: [change] }),
      "GET /api/v1/changes?view=mine&page_number=1&page_size=50": () => json(emptyPage),
    });
    renderWithProviders(<ChangesPage />);
    const user = userEvent.setup();

    expect(await screen.findByRole("region", { name: "Changes you may approve" })).toBeInTheDocument();
    await waitFor(() =>
      expect(calls.some((call) => call.key === "GET /api/v1/changes?view=to_approve&page_number=1&page_size=50")).toBe(
        true,
      ),
    );
    const views = screen.getByRole("radiogroup", { name: "Which changes" });
    expect(within(views).queryByRole("radio", { name: "All" })).not.toBeInTheDocument();

    await user.click(within(views).getByRole("radio", { name: "Mine" }));
    await waitFor(() =>
      expect(calls.some((call) => call.key === "GET /api/v1/changes?view=mine&page_number=1&page_size=50")).toBe(true),
    );
  });

  it("shows a change's state before and after side by side, the changed fields marked in words [TF-068 AC1, ADR-0052]", () => {
    expect(changeRows(change.before, change.after)).toEqual([
      { field: "code", before: "buyer", after: "buyer", changed: false },
      { field: "name", before: "Buyer", after: "Buyer", changed: false },
      {
        field: "permissions",
        before: ["orders.read"],
        after: ["orders.read", "orders.approve"],
        changed: true,
      },
    ]);
    // A role made: nothing before; a setting: its value alone.
    expect(changeRows(null, { code: "auditor" })).toEqual([
      { field: "code", before: undefined, after: "auditor", changed: true },
    ]);
    expect(changeRows({ value: 12 }, { value: 14 })).toEqual([
      { field: "value", before: 12, after: 14, changed: true },
    ]);
  });

  it("lets a second administrator approve with a reason, and the change applies [TF-068 AC3, ADR-0052]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(approver),
      "GET /api/v1/changes/c-1": () => json(change),
      "POST /api/v1/changes/c-1/approve": () =>
        json({
          ...change,
          status: "applied",
          may_decide: false,
          decided_by: approver.id,
          decider_name: "Bea Approver",
        }),
    });
    renderRecord();
    const user = userEvent.setup();

    expect(await screen.findByRole("heading", { name: "Change the role Buyer" })).toBeInTheDocument();
    const table = screen.getByRole("table", { name: /before the change and after it/ });
    const permissions = within(table).getByText("permissions").closest("tr");
    expect(permissions).toHaveTextContent("Changed");
    expect(permissions).toHaveTextContent("orders.read, orders.approve");

    await user.click(screen.getByRole("button", { name: "Approve" }));
    const dialog = await screen.findByRole("dialog", { name: "Approve the change" });
    await fill(user, within(dialog).getByLabelText(/^why you approve it/i), "Agreed with finance.");
    await user.click(within(dialog).getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    const sent = calls.find((call) => call.key === "POST /api/v1/changes/c-1/approve");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({ reason: "Agreed with finance." });
  });

  it("offers the requester only to withdraw, never to approve their own change [TF-068 AC2, ADR-0052]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(requester),
      "GET /api/v1/changes/c-1": () => json({ ...change, may_decide: false }),
    });
    renderRecord();

    expect(await screen.findByRole("heading", { name: "Change the role Buyer" })).toBeInTheDocument();
    expect(screen.getByText(/You asked for this change, so someone else approves it/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Approve" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Reject" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Withdraw" })).toBeInTheDocument();
  });

  it("asks for a signature when the tenant signs approvals in the change's area, with the reason [TF-068 AC3, ADR-0052]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(approver),
      "GET /api/v1/changes/c-1": () => json({ ...change, signed: true }),
      "GET /api/v1/signatures/challenge?record_type=configuration_change": () =>
        json({
          first_in_session: false,
          login_id: false,
          password: true,
          second_factor: false,
          methods: [],
          passkey_options: null,
          provider_id: null,
          provider_name: null,
          provider_confirmed: false,
          reauthenticate: true,
          policy: { required: true, meanings: ["approved", "rejected"], reauthenticate: true },
        }),
      "POST /api/v1/changes/c-1/reject": () => json({ ...change, status: "rejected", may_decide: false }),
    });
    renderRecord();
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Reject" }));
    const reason = await screen.findByRole("dialog", { name: "Reject the change" });
    await fill(user, within(reason).getByLabelText(/^why you reject it/i), "Not before the audit.");
    await user.click(within(reason).getByRole("button", { name: "Continue to sign" }));

    const signing = await screen.findByRole("dialog", { name: "Sign your rejection" });
    expect(signing).toHaveTextContent("Rejected");
    await fill(user, await within(signing).findByLabelText(/^password/i), "correct horse battery staple");
    await user.click(within(signing).getByRole("button", { name: "Sign" }));

    await waitFor(() => expect(calls.some((call) => call.key === "POST /api/v1/changes/c-1/reject")).toBe(true));
    const sent = calls.find((call) => call.key === "POST /api/v1/changes/c-1/reject");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({
      reason: "Not before the audit.",
      signature: { password: "correct horse battery staple" },
    });
  });

  it("says a change that changed meanwhile went back for a new review [TF-068 AC3, ADR-0052]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(approver),
      "GET /api/v1/changes/c-1": () =>
        json({
          ...change,
          reviews: 1,
          restarted_at: "2026-10-23T10:00:00Z",
          restart_note: "The role changed after this change was asked for. Review it again as it now stands.",
        }),
    });
    renderRecord();

    expect(await screen.findByText(/The role changed after this change was asked for/)).toBeInTheDocument();
    expect(screen.getByText("Back for a new review")).toBeInTheDocument();
  });

  it("tells the person, in the frame, that their change waits for approval, with a link to it [TF-068 AC1, ADR-0052]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(requester),
      "PUT /api/v1/roles/r-1": () => problem(202, { error_code: "changes.change.held", change_id: "c-1" }),
    });
    renderWithProviders(<HeldChangeNotice />);

    await request("/api/v1/roles/r-1", { method: "PUT", body: {} }).catch(() => undefined);

    const notice = await screen.findByRole("status");
    expect(notice).toHaveTextContent("Your change waits for a second administrator's approval.");
    expect(within(notice).getByRole("link", { name: "Open the change" })).toHaveAttribute(
      "href",
      "/console/changes/c-1",
    );
  });

  it("shows the four-eyes areas in words, and none as every change applying at once [TF-068 AC1, ADR-0052]", () => {
    const setting = { key: "changes.four_eyes.areas", type: "json" } as SettingView;
    const t = i18n.t.bind(i18n);
    expect(displayValue(setting, ["roles", "single_sign_on"], t)).toBe("Roles and their assignments, Single sign-on");
    expect(displayValue(setting, [], t)).toBe("None: every change applies at once");
  });
});

describe("my pending changes", () => {
  it("lists a person's own changes that wait, without any permission, and withdraws one [TF-068 AC1, ADR-0052]", async () => {
    const plain = { ...me([]), id: "u-requester" };
    let withdrawn = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(plain),
      "GET /api/v1/changes?view=mine&status=pending&page_size=100": () =>
        json({
          ...emptyPage,
          data: withdrawn ? [] : [change],
          meta: { ...emptyPage.meta, total_count: withdrawn ? 0 : 1 },
        }),
      "POST /api/v1/changes/c-1/withdraw": () => {
        withdrawn = true;
        return json({ ...change, status: "withdrawn" });
      },
    });
    renderWithProviders(<MyPendingChanges />);
    const user = userEvent.setup();

    const list = await screen.findByRole("list", { name: "My pending changes" });
    expect(within(list).getByRole("link", { name: "Change the role Buyer" })).toHaveAttribute(
      "href",
      "/console/changes/c-1",
    );
    expect(list).toHaveTextContent("Roles and their assignments.");

    await user.click(within(list).getByRole("button", { name: "Withdraw: Change the role Buyer" }));
    const dialog = await screen.findByRole("dialog", { name: "Withdraw this change?" });
    await user.click(within(dialog).getByRole("button", { name: "Withdraw" }));

    expect(await screen.findByText("No change of yours waits for approval.")).toBeInTheDocument();
    expect(calls.some((call) => call.key === "POST /api/v1/changes/c-1/withdraw")).toBe(true);
  });
});
