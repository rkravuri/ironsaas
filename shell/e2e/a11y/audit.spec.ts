import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { administrator, envelope, me, record, stubAccess, units } from "./stubs";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

// Every time on these pages is in the person's zone with its name (TF-065 AC2): the browser's zone is set, so the
// screenshots show one that is not UTC.
test.use({ timezoneId: "Asia/Kolkata" });

async function expectNoViolation(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
  expect(
    results.violations.map(
      (violation) =>
        `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => node.target.map(String).join(" ")).join("; ")}`,
    ),
  ).toEqual([]);
}

const ago = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();
const hash = (digit: string) => digit.repeat(64);

const event = (n: number, type: string, minutes: number, extra: Record<string, unknown> = {}) => ({
  id: `01a18000-0000-7000-8000-${String(n).padStart(12, "0")}`,
  event_type: type,
  outcome: "success",
  actor_id: me.id,
  actor_name: "Admin",
  source: "ui",
  target: record.display_name,
  record_type: "principal",
  record_id: record.id,
  reason: null,
  correlation_id: `c-${n}`,
  details: null,
  created_at: ago(minutes),
  created_by: me.id,
  severity: "normal",
  chain_position: 1200 - n,
  ...extra,
});

const events = [
  event(1, "identity.user.deactivated", 5, { reason: "Left the company", severity: "high" }),
  event(2, "authz.assignment.granted", 30, { target: "Unit user reader" }),
  event(3, "identity.session.ended", 90, {
    impersonator_id: "01a18000-0000-7000-8000-0000000000f1",
    impersonator_name: "Olga Operator",
  }),
  event(4, "identity.sign_in.failed", 200, { outcome: "failure", source: "api", chain_position: null }),
  event(5, "identity.user.invited", 60 * 24 * 3),
];

const archived = [1, 2, 3].map((n) => ({
  ...event(10 + n, "identity.user.invited", 0),
  created_at: `2025-01-0${5 + n}T09:30:00Z`,
  chain_position: n,
  archived: true,
}));

const chain = {
  head_position: 1204,
  head_hash: hash("a"),
  head_sealed_at: ago(1),
  unsealed: 3,
  archived_up_to: 800,
  pruned_up_to: 0,
  seals: [
    { first_position: 1201, last_position: 1204, last_hash: hash("a"), sealed_at: ago(1) },
    { first_position: 1190, last_position: 1200, last_hash: hash("b"), sealed_at: ago(2) },
    { first_position: 1180, last_position: 1189, last_hash: hash("c"), sealed_at: ago(3) },
  ],
  anchored: false,
  anchored_up_to: 1200,
  latest_verification: {
    result: "failed",
    full: true,
    from_position: 1,
    to_position: 1204,
    events_checked: 1203,
    problems: [
      {
        kind: "modified",
        position: 17,
        event_id: "01a18000-0000-7000-8000-000000000017",
        message: "The content hash differs.",
      },
      { kind: "missing", position: 18, event_id: null, message: "No event at position 18." },
    ],
    restore_position: 1100,
    restore_at: ago(60 * 10),
    verified_at: ago(60 * 2),
  },
  segment: 1,
  restores: [] as unknown[],
};

/** The same chain after an operator accepted the restore: the fleet's next full run passed, and the restore stays shown. */
const acceptedChain = {
  ...chain,
  segment: 2,
  latest_verification: {
    result: "passed",
    full: true,
    from_position: 1,
    to_position: 1204,
    events_checked: 1204,
    problems: [],
    restore_position: null,
    restore_at: null,
    verified_at: ago(30),
  },
  restores: [
    {
      segment: 2,
      start_position: 1101,
      restore_position: 1100,
      restore_at: ago(60 * 10),
      lost_from: 1101,
      lost_to: 1180,
      ticket: "INC-77",
      reason: "Restored from last night's backup after the disk failure",
      accepted_by: "Olga Operator",
      accepted_at: ago(40),
    },
  ],
};

const impersonators = {
  data: [
    { id: "01a18000-0000-7000-8000-0000000000f1", name: "Olga Operator", kind: "operator", field: "impersonator_id" },
    {
      id: "01a18000-0000-7000-8000-0000000000b9",
      name: "Break-glass access (Olga Operator)",
      kind: "break_glass",
      field: "actor_id",
    },
    { id: me.id, name: "Admin", kind: "person", field: "impersonator_id" },
  ],
};

const holds = [
  {
    id: "01a18100-0000-7000-8000-000000000001",
    scope: "master",
    record_type: "supplier",
    record_id: "01a18100-0000-7000-8000-0000000000a1",
    reason: "Regulator's inquiry 2026-14",
    placed_at: ago(60 * 24 * 2),
    placed_by: me.id,
    placed_by_name: "Admin",
    released_at: null,
    release_reason: null,
  },
  {
    id: "01a18100-0000-7000-8000-000000000002",
    scope: "tenant",
    record_type: null,
    record_id: null,
    reason: "Annual audit",
    placed_at: ago(60 * 24 * 40),
    placed_by: me.id,
    placed_by_name: "Admin",
    released_at: ago(60 * 24 * 10),
    release_reason: "Audit closed",
  },
];

const retention = {
  classes: [
    ["business_document", 2555, 365, 10950, []],
    ["session", 365, 30, 1095, ["session"]],
    ["security_event", 730, 90, 3650, []],
    ["notification", 365, 30, 1095, []],
    ["audit_event", 2555, 2555, 10950, ["audit_event"]],
    ["audit_online", 390, 30, 3650, []],
  ].map(([code, days, minimum, maximum, types]) => ({
    code,
    description: String(code),
    default_days: days,
    minimum_days: minimum,
    maximum_days: maximum,
    days: code === "session" ? 180 : days,
    chosen: code === "session",
    record_types: types,
  })),
};

/** An administrator who reads the audit trail, places holds and chooses retention, with the console's stubs. */
async function stubAudit(page: Page): Promise<void> {
  await stubAccess(page);
  const permissions = [...administrator, "audit.events.read", "audit.legal_holds.manage", "audit.retention.manage"];
  await page.route("**/api/v1/me", (route) =>
    route.fulfill({
      json: {
        ...me,
        permissions,
        permissions_anywhere: permissions,
        home_unit: { id: units[0].id, name: units[0].name, code: units[0].code },
      },
    }),
  );
  await page.route("**/api/v1/me/access?**", (route) => route.fulfill({ json: { scope: "unit", permissions } }));
  await page.route("**/api/v1/audit-events?**", (route) =>
    route.fulfill({ json: envelope(route.request().url(), events) }),
  );
  await page.route("**/api/v1/audit-events/archived?**", (route) =>
    route.fulfill({ json: { data: archived, batches_read: 1, truncated: false } }),
  );
  await page.route("**/api/v1/audit-events/impersonators", (route) => route.fulfill({ json: impersonators }));
  await page.route("**/api/v1/audit-chain", (route) => route.fulfill({ json: chain }));
  await page.route("**/api/v1/legal-holds", (route) => route.fulfill({ json: { holds } }));
  await page.route("**/api/v1/retention", (route) => route.fulfill({ json: retention }));
  await page.route("**/api/v1/role-assignments/mine", (route) => route.fulfill({ json: { data: [] } }));
  await page.route("**/api/v1/delegations/mine", (route) => route.fulfill({ json: { given: [], received: [] } }));
}

for (const scheme of schemes) {
  test(`the audit trail's search and its archive in the ${scheme} scheme, every time with its zone, with no WCAG 2.1 AA violation [TF-064 AC2, TF-065 AC2, TF-172 AC1, ADR-0024, ADR-0050]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubAudit(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/audit");
    const grid = page.getByRole("region", { name: "Audit events" });
    await expect(grid.locator('.ag-row[row-index="0"]').first()).toContainText("identity.user.deactivated");
    await expect(grid.locator('.ag-row[row-index="0"]').first()).toContainText("GMT+5:30");
    await expect(grid.locator('.ag-row[row-index="2"]').first()).toContainText("Olga Operator signed in as Admin");
    // When, with its zone, and who read in full at 1280 pixels: no cell of theirs is cut short.
    for (const column of ["created_at", "actor_id"]) {
      for (const cell of await grid.locator(`.ag-row .ag-cell[col-id="${column}"]`).all()) {
        expect(await cell.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
      }
    }
    await page.screenshot({ path: `test-results/a11y/audit-search-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // Who signed in as someone: the operator, the break-glass accounts and the people, by name and grouped.
    await page.getByRole("combobox", { name: "Signed in as them by" }).click();
    const offered = page.getByRole("listbox");
    await expect(offered.getByRole("option", { name: "Break-glass access (Olga Operator)" })).toBeVisible();
    await expect(offered).toContainText("The platform's operators");
    await page.screenshot({ path: `test-results/a11y/audit-signed-in-by-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");

    await page.getByRole("tab", { name: "Archive" }).click();
    await page.getByLabel("From", { exact: true }).fill("2025-01-06");
    await page.getByLabel("To", { exact: true }).fill("2025-01-08");
    await page.getByRole("button", { name: "Search the archive" }).click();
    const table = page.getByRole("table", { name: /3 archived events/ });
    await expect(table).toContainText("Jan 6, 2025, 3:00 PM GMT+5:30");
    await page.screenshot({ path: `test-results/a11y/audit-archive-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`the audit chain's status after a failed verification and a restore in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-061 AC2, TF-061 AC3, TF-172 AC1, ADR-0024, ADR-0050]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubAudit(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/audit/chain");
    await expect(page.locator("[data-verification-result=failed]")).toHaveText("Failed");
    const problems = page.getByRole("table", { name: "2 problems found" });
    await expect(problems).toContainText("01a18000-0000-7000-8000-000000000017");
    await expect(page.locator("[data-restore-point]")).toContainText("whole up to position 1100");
    await page.screenshot({ path: `test-results/a11y/audit-chain-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`the audit chain's status after an accepted restore in the ${scheme} scheme: passed, with the restore and what it lost still shown, and no WCAG 2.1 AA violation [TF-061 AC3, TF-065 AC2, TF-172 AC1, ADR-0024, ADR-0050]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubAudit(page);
    await page.route("**/api/v1/audit-chain", (route) => route.fulfill({ json: acceptedChain }));
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/audit/chain");
    await expect(page.locator("[data-verification-result=passed]")).toHaveText("Passed");
    const restores = page.getByRole("table", { name: "1 accepted restore" });
    await expect(restores).toContainText("Positions 1101 to 1180");
    await expect(restores).toContainText("By Olga Operator, ticket INC-77");
    await expect(restores).toContainText("GMT+5:30");
    await page.screenshot({ path: `test-results/a11y/audit-chain-accepted-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`legal holds and placing one with a reason in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-172 AC1, ADR-0024, ADR-0050]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubAudit(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/audit/legal-holds");
    await expect(page.getByRole("table", { name: "1 standing hold" })).toContainText("Regulator's inquiry 2026-14");
    await expect(page.getByRole("table", { name: "1 released hold" })).toContainText("Audit closed");
    await page.screenshot({ path: `test-results/a11y/legal-holds-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Place a legal hold" }).click();
    const dialog = page.getByRole("dialog", { name: "Place a legal hold" });
    await dialog.getByRole("button", { name: "Place the hold" }).click();
    await expect(dialog.getByText("Say which kind of record it is.")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/legal-holds-place-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`retention and a period below the operator's minimum refused in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-066 AC3, TF-172 AC1, ADR-0024, ADR-0050]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubAudit(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/audit/retention");
    const table = page.getByRole("table", { name: "Retention classes, their period and limits" });
    await expect(table).toContainText("Audit trail online");
    await page.screenshot({ path: `test-results/a11y/retention-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Change the retention of Sessions" }).click();
    const dialog = page.getByRole("dialog", { name: "Retention of Sessions" });
    await dialog.getByLabel(/^Days/).fill("10");
    await dialog.getByLabel(/^Why it changes/).fill("Shorter sessions");
    await dialog.getByRole("button", { name: "Save" }).click();
    await expect(dialog.getByText("The operator's minimum is 30 days. Choose that or more.")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/retention-change-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`a user record's History tab, newest first with the zone, in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-064 AC1, TF-065 AC2, TF-172 AC1, ADR-0024, ADR-0050]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubAudit(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto(`/users/${record.id}`);
    await page.getByRole("tab", { name: "History" }).click();
    const history = page.locator("[data-panel=history]");
    await expect(history).toContainText("identity.user.deactivated");
    await expect(history).toContainText("GMT+5:30");
    await page.screenshot({ path: `test-results/a11y/user-history-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`the time zone choice on My profile in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-065 AC2, TF-172 AC1, ADR-0024, ADR-0050]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubAudit(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/me?tab=preferences");
    const zone = page.getByRole("combobox", { name: "Time zone" });
    // ICU names the zone Asia/Calcutta in some builds and Asia/Kolkata in others.
    await expect(zone).toHaveValue(/^This browser's \(Asia\/(Kolkata|Calcutta)\)$/);
    await expect(
      page.getByText(/Every time the shell shows is in this zone, with its name\. Now it is .* GMT\+5:30\./),
    ).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/profile-time-zone-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });
}
