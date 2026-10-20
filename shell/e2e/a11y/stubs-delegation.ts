import type { Page } from "@playwright/test";
import { administrator, me, sessionFromNow, stubConsole, units } from "./stubs";

const ada = { id: "01a17d00-0000-7000-8000-0000000000a1", name: "Ada Lovelace" };
const grace = { id: "01a17d00-0000-7000-8000-0000000000a2", name: "Grace Hopper" };
const linus = { id: "01a17d00-0000-7000-8000-0000000000a3", name: "Linus Pauling" };
const self = { id: me.id, name: me.display_name };
const userAdministration = { code: "identity.user_administration", name: "user administration" };
const days = (n: number) => new Date(Date.now() + n * 24 * 60 * 60_000).toISOString();
const listOf = <T>(data: T[]) => ({
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

const delegationRow = (
  n: number,
  delegator: typeof ada,
  delegate: typeof ada,
  status: string,
  from: number,
  to: number,
) => ({
  id: `01a17d00-0000-7000-8000-${String(n).padStart(12, "0")}`,
  delegator,
  delegate,
  duties: [userAdministration],
  starts_at: days(from),
  ends_at: days(to),
  reason: n % 2 === 0 ? "Annual leave." : "A week at the Frankfurt fair.",
  status,
  ended_at: null,
  end_reason: null,
  created_by: delegator.id,
  created_at: days(from - 1),
});

/** The delegations the administrator gives and holds, and those of their units' people. */
const myDelegations = [
  delegationRow(1, ada, self, "active", -2, 5),
  delegationRow(2, self, grace, "scheduled", 10, 17),
  delegationRow(3, self, linus, "expired", -30, -20),
];
const managedDelegations = [
  ...myDelegations,
  delegationRow(4, grace, linus, "active", -1, 3),
  delegationRow(5, linus, ada, "scheduled", 4, 9),
];

const actingFor = { id: ada.id, name: ada.name, duties: [userAdministration], until: days(5) };
const held = {
  given: [
    {
      delegator_id: self.id,
      delegator_name: self.name,
      delegate_id: grace.id,
      delegate_name: grace.name,
      duties: [userAdministration],
      until: days(17),
    },
  ],
  received: [
    {
      delegator_id: ada.id,
      delegator_name: ada.name,
      delegate_id: self.id,
      delegate_name: self.name,
      duties: [userAdministration],
      until: actingFor.until,
    },
  ],
};

const reviewer = [...administrator, "authz.delegations.manage", "authz.reviews.decide", "authz.reviews.manage"];

/**
 * A signed-in administrator who holds a delegation from Ada Lovelace (ADR-0037), with delegations, duties, the people
 * directory and acting for from memory. The choice to act is remembered, so /me answers it after, as the API does.
 */
export async function stubDelegations(target: Page, { acting = false }: { acting?: boolean } = {}): Promise<void> {
  await stubConsole(target);
  let actingNow = acting;
  const meNow = () => ({
    ...me,
    session: sessionFromNow(),
    permissions: reviewer,
    permissions_anywhere: reviewer,
    home_unit: { id: units[0].id, name: units[0].name, code: units[0].code },
    acting_for: actingNow ? actingFor : null,
    delegations: held,
  });
  await target.route("**/api/v1/me", (route) => route.fulfill({ json: meNow() }));
  await target.route("**/api/v1/me/acting-for", (route) => {
    actingNow = route.request().method() === "POST";
    return route.fulfill({ json: meNow() });
  });
  await target.route("**/api/v1/duties", (route) =>
    route.fulfill({
      json: {
        data: [
          {
            code: "all",
            module: "kernel",
            name: "all duties",
            description: "Everything the person may do, at every scope they may do it.",
            permissions: [],
          },
          {
            ...userAdministration,
            module: "identity",
            description:
              "Reading and managing people: their accounts, their invitations, and the roles they hold where.",
            permissions: ["identity.users.read", "identity.users.manage"],
          },
        ],
      },
    }),
  );
  await target.route("**/api/v1/delegations?**", (route) => route.fulfill({ json: listOf(managedDelegations) }));
  await target.route("**/api/v1/delegations/mine", (route) => route.fulfill({ json: { data: myDelegations } }));
  await target.route("**/api/v1/directory?**", (route) =>
    route.fulfill({
      json: listOf([
        { id: grace.id, display_name: grace.name, home_unit: "North" },
        { id: linus.id, display_name: linus.name, home_unit: "Finance" },
      ]),
    }),
  );
}

export const reviewId = "01a17e00-0000-7000-8000-000000000001";
const progress = (decided: number, total: number, overdue = 0) => ({
  total,
  decided,
  confirmed: decided,
  revoked: 0,
  open: total - decided,
  overdue,
});

/** Campaigns, one campaign with its units and items, for the review pages (TF-038). */
export async function stubReviews(target: Page): Promise<void> {
  await stubDelegations(target);
  const summaries = [
    {
      id: reviewId,
      name: "Quarterly review, Operations",
      due_on: days(9).slice(0, 10),
      status: "open",
      overdue: false,
      started_at: days(-5),
      progress: progress(3, 8),
    },
    {
      id: "01a17e00-0000-7000-8000-000000000002",
      name: "Finance year end",
      due_on: days(-3).slice(0, 10),
      status: "open",
      overdue: true,
      started_at: days(-40),
      progress: progress(10, 14, 4),
    },
    {
      id: "01a17e00-0000-7000-8000-000000000003",
      name: "Spring review, Sales",
      due_on: days(-120).slice(0, 10),
      status: "closed",
      overdue: false,
      started_at: days(-160),
      progress: progress(22, 22),
    },
  ];
  await target.route("**/api/v1/access-reviews?**", (route) => route.fulfill({ json: listOf(summaries) }));

  const path = (unit: (typeof units)[number]) => {
    const steps = [unit];
    for (
      let parent = units.find((candidate) => candidate.id === unit.parent_id);
      parent;
      parent = units.find((candidate) => candidate.id === parent?.parent_id)
    ) {
      steps.unshift(parent);
    }
    return steps.map((step) => ({ id: step.id, name: step.name }));
  };
  const [operations, north, south] = [units[3], units[4], units[5]];
  // The last is the viewer's own role, which another reviewer decides; the one before it only the review's owner may decide (ADR-0037).
  const people = [
    "Ada Lovelace",
    "Grace Hopper",
    "Linus Pauling",
    "Marie Curie",
    "Alan Turing",
    "Katherine Johnson",
    "Edsger Dijkstra",
    me.display_name,
  ];
  const items = people.map((name, index) => {
    const unit = [operations, north, south][index % 3] ?? operations;
    const n = String(index).padStart(2, "0");
    return {
      id: `01a17e00-0000-7000-8000-0000000001${n}`,
      review_id: reviewId,
      assignment_id: `01a17e00-0000-7000-8000-0000000002${n}`,
      principal_id: `01a17e00-0000-7000-8000-0000000003${n}`,
      principal_name: name,
      role_id: "01a15200-0000-7000-8000-000000000004",
      role_code: index % 2 === 0 ? "store-clerk" : "store-manager",
      role_name: index % 2 === 0 ? "Store clerk" : "Store manager",
      scope_kind: index % 4 === 0 ? "unit_with_children" : "unit",
      unit_id: unit.id,
      unit_name: unit.name,
      unit_path: path(unit),
      decision:
        index < 3
          ? {
              decision: index === 1 ? "revoke" : "confirm",
              comment: null,
              decided_by: grace,
              decided_at: days(-1),
              assignment_removed: index === 1,
            }
          : null,
      yours: index === 7,
      with_owner: index === 6,
    };
  });
  const byUnit = [operations, north, south].map((unit) => {
    const mine = items.filter((item) => item.unit_id === unit.id);
    const decided = mine.filter((item) => item.decision !== null).length;
    return {
      unit_id: unit.id,
      unit_name: unit.name,
      unit_path: path(unit).map((step) => step.name),
      progress: progress(decided, mine.length),
      escalation: null,
    };
  });
  await target.route(`**/api/v1/access-reviews/${reviewId}`, (route) =>
    route.fulfill({
      json: {
        ...summaries[0],
        owner: grace,
        units: [{ unit_id: operations.id, name: operations.name, includes_children: true }],
        closed_at: null,
        by_unit: byUnit,
        can_manage: true,
      },
    }),
  );
  await target.route(`**/api/v1/access-reviews/${reviewId}/items?**`, (route) => {
    const state = new URL(route.request().url()).searchParams.get("state");
    return route.fulfill({
      json: listOf(
        items.filter((item) =>
          state === "open" ? item.decision === null : state === "decided" ? item.decision !== null : true,
        ),
      ),
    });
  });
}
