import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { DelegationsPage } from "./DelegationsPage";

const person = me();
const ada = { id: "01a17c00-0000-7000-8000-0000000000a1", name: "Ada Lovelace" };
const grace = { id: "01a17c00-0000-7000-8000-0000000000a2", name: "Grace Hopper" };
const admin = { id: person.id, name: "Admin" };
const delegation = (id: string, delegator: typeof ada, delegate: typeof ada, status: string) => ({
  id,
  delegator,
  delegate,
  duties: [{ code: "identity.user_administration", name: "user administration" }],
  starts_at: "2026-10-20T09:00:00Z",
  ends_at: "2026-10-28T00:00:00Z",
  reason: "Annual leave.",
  status,
  ended_at: null,
  end_reason: null,
  created_by: delegator.id,
  created_at: "2026-10-20T09:00:00Z",
});
const given = delegation("01a17c00-0000-7000-8000-0000000000d1", admin, grace, "scheduled");
const received = delegation("01a17c00-0000-7000-8000-0000000000d2", ada, admin, "active");
const emptyList = { data: [], meta: { page_number: 1, page_size: 50, total_count: 0, total_pages: 1, has_previous: false, has_next: false, page_size_capped: false, max_page_size: 200 }, links: { self: "x", first: "x", previous: null, next: null, last: "x" } };

function show(permissions: string[] = []) {
  const stub = stubFetch({
    "GET /api/v1/me": () => json(me(permissions)),
    "GET /api/v1/delegations/mine": () => json({ data: [given, received] }),
    "GET /api/v1/delegations?page_number=1&page_size=50&sort=-starts_at": () => json(emptyList),
    [`POST /api/v1/delegations/${given.id}/end`]: () => json({ ...given, status: "ended", ended_at: "2026-10-20T10:00:00Z" }),
  });
  renderWithProviders(<DelegationsPage />, { route: "/console/delegations" });
  return stub;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the delegations page (TF-037)", () => {
  it("shows what the person gives and holds in words, with Act for on the active one held", async () => {
    show();

    expect(await screen.findByText("You hand user administration to Grace Hopper")).toBeInTheDocument();
    expect(screen.getByText("Ada Lovelace hands you user administration")).toBeInTheDocument();
    expect(screen.getByText("Scheduled")).toBeInTheDocument();
    expect(screen.getByText("Active")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Act for Ada Lovelace/ })).toBeInTheDocument();
    // Others' delegations are for administrators only.
    expect(screen.queryByRole("heading", { name: "Others' delegations" })).not.toBeInTheDocument();
  });

  it("ends a delegation behind a confirmation", async () => {
    const { calls } = show();
    const user = userEvent.setup();
    await screen.findByText("You hand user administration to Grace Hopper");

    await user.click(screen.getAllByRole("button", { name: "End now" })[0] as HTMLElement);
    const dialog = await screen.findByRole("dialog", { name: "End this delegation" });
    expect(within(dialog).getByText("Grace Hopper stops acting for Admin at once, and cannot choose to again. The delegation stays in the history.")).toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "End now" }));

    await waitFor(() => expect(calls.map((call) => call.key)).toContain(`POST /api/v1/delegations/${given.id}/end`));
  });

  it("shows others' delegations to an administrator, with delegating for someone", async () => {
    show(["authz.delegations.manage"]);

    expect(await screen.findByRole("heading", { name: "Others' delegations" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Delegate for someone" })).toBeInTheDocument();
  });
});
