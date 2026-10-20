import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { ReviewRecordPage } from "./ReviewRecordPage";

const reviewer = ["authz.reviews.decide"];
const review = "01a17b00-0000-7000-8000-000000000001";
const progress = (decided: number, total: number) => ({ total, decided, confirmed: decided, revoked: 0, open: total - decided, overdue: 0 });
const record = {
  id: review,
  name: "Quarterly review",
  due_on: "2026-11-03",
  status: "open",
  overdue: false,
  owner: { id: "01a17b00-0000-7000-8000-0000000000b1", name: "Admin" },
  units: [{ unit_id: "01a17b00-0000-7000-8000-0000000000c1", name: "North", includes_children: true }],
  started_at: "2026-10-20T09:00:00Z",
  closed_at: null,
  progress: progress(1, 3),
  by_unit: [{ unit_id: "01a17b00-0000-7000-8000-0000000000c2", unit_name: "Store 12", unit_path: ["Acme", "North", "Store 12"], progress: progress(1, 3), escalation: null }],
  can_manage: false,
};
const item = (id: string, name: string, decided: boolean, yours = false) => ({
  id,
  review_id: review,
  assignment_id: `${id.slice(0, -2)}aa`,
  principal_id: `${id.slice(0, -2)}bb`,
  principal_name: name,
  role_id: "01a17b00-0000-7000-8000-0000000000d1",
  role_code: "store-clerk",
  role_name: "Store clerk",
  scope_kind: "unit",
  unit_id: "01a17b00-0000-7000-8000-0000000000c2",
  unit_name: "Store 12",
  unit_path: [
    { id: "01a17b00-0000-7000-8000-0000000000c0", name: "Acme" },
    { id: "01a17b00-0000-7000-8000-0000000000c1", name: "North" },
    { id: "01a17b00-0000-7000-8000-0000000000c2", name: "Store 12" },
  ],
  decision: decided ? { decision: "confirm", comment: null, decided_by: { id: "x", name: "Grace" }, decided_at: "2026-10-20T10:00:00Z", assignment_removed: false } : null,
  yours,
  with_owner: false,
});
const list = (data: unknown[]) => ({
  data,
  meta: { page_number: 1, page_size: 50, total_count: data.length, total_pages: 1, has_previous: false, has_next: false, page_size_capped: false, max_page_size: 200 },
  links: { self: "x", first: "x", previous: null, next: null, last: "x" },
});
const items = `/api/v1/access-reviews/${review}/items`;
const others = [item("01a17b00-0000-7000-8000-0000000000e1", "Bob Builder", false), item("01a17b00-0000-7000-8000-0000000000e2", "Carol Clerk", false)];
/** The reviewer's own role in the unit: they read it and never decide it (ADR-0037). */
const own = item("01a17b00-0000-7000-8000-0000000000e4", "Admin", false, true);
const open = [...others, own];
const decided = [item("01a17b00-0000-7000-8000-0000000000e3", "Dan Decided", true)];
/** The page renders the grid, the heaviest thing in the shell, so these tests wait longer than the default. */
const heavy = { timeout: 10_000 };
const heavyTest = 20_000;

function show() {
  const stub = stubFetch({
    "GET /api/v1/me": () => json(me(reviewer)),
    [`GET /api/v1/access-reviews/${review}`]: () => json(record),
    [`GET ${items}?state=open&page_number=1&page_size=50&sort=unit_name`]: () => json(list(open)),
    [`GET ${items}?state=decided&page_number=1&page_size=50&sort=unit_name`]: () => json(list(decided)),
    [`POST /api/v1/access-reviews/${review}/confirm`]: () => json({ confirmed: 2, already_decided: 0 }),
  });
  renderWithProviders(
    <Routes>
      <Route path="/console/access-reviews/:id" element={<ReviewRecordPage />} />
    </Routes>,
    { route: `/console/access-reviews/${review}` },
  );
  return stub;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the access review page (TF-038)", () => {
  it(
    "asks for the items still to decide, which the API keeps to the reviewer's units, and offers to confirm that page without the reviewer's own",
    async () => {
      const { calls } = show();

      expect(await screen.findByRole("heading", { name: "Quarterly review" }, heavy)).toBeInTheDocument();
      expect(await screen.findByRole("button", { name: "Confirm the 2 items on this page" }, heavy)).toBeEnabled();
      expect(calls.map((call) => call.key)).toContain(`GET ${items}?state=open&page_number=1&page_size=50&sort=unit_name`);
      // A reviewer who does not manage the campaign cannot close it.
      expect(screen.queryByRole("button", { name: "Close the review" })).not.toBeInTheDocument();
    },
    heavyTest,
  );

  it(
    "asks for the decided items when the reviewer chooses them, and has nothing on that page to confirm",
    async () => {
      const { calls } = show();
      const user = userEvent.setup();
      await screen.findByRole("button", { name: "Confirm the 2 items on this page" }, heavy);

      await user.click(screen.getByText("Decided"));

      await waitFor(() => expect(calls.map((call) => call.key)).toContain(`GET ${items}?state=decided&page_number=1&page_size=50&sort=unit_name`));
      expect(await screen.findByRole("button", { name: "Confirm the 0 items on this page" }, heavy)).toBeDisabled();
    },
    heavyTest,
  );

  it(
    "confirms the page's open items behind a confirmation, and says how many",
    async () => {
      const { calls } = show();
      const user = userEvent.setup();

      await user.click(await screen.findByRole("button", { name: "Confirm the 2 items on this page" }, heavy));
      expect(await screen.findByText("The 2 items still to decide on this page keep their access, and your decision is recorded for each.")).toBeInTheDocument();
      await user.click(screen.getByRole("button", { name: "Confirm 2 items" }));

      expect(await screen.findByText("Confirmed 2 items.")).toBeInTheDocument();
      const posted = calls.find((call) => call.key === `POST /api/v1/access-reviews/${review}/confirm`);
      // The reviewer's own row is left out: another reviewer decides it.
      expect(JSON.parse(posted?.init?.body as string)).toEqual({ item_ids: others.map((row) => row.id), comment: null });
    },
    heavyTest,
  );
});
