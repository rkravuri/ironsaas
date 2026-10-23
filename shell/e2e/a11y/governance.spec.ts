import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { administrator, context, envelope, me, stubSettings, tenantSettings, units } from "./stubs";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

// Every time is in the person's zone with its name (TF-065 AC2): the screenshots show one that is not UTC.
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
const later = (days: number) => new Date(Date.now() + days * 86_400_000).toISOString();
const utcDay = (offsetDays: number) => new Date(Date.now() + offsetDays * 86_400_000).toISOString().slice(0, 10);

const permissions = [
  ...administrator,
  "changes.changes.read",
  "compliance.evidence.export",
  "compliance.validation.read",
  "settings.values.manage",
  "settings.values.read",
];

const change = {
  id: "01a1a000-0000-7000-8000-0000000000c1",
  area: "roles",
  kind: "authz.role.update",
  subject: "role:01a1a000-0000-7000-8000-0000000000r1",
  title: "Change the role Buyer",
  before: {
    code: "buyer",
    name: "Buyer",
    description: "Raises orders for their unit.",
    permissions: ["orders.read", "orders.create"],
  },
  after: {
    code: "buyer",
    name: "Buyer",
    description: "Raises orders for their unit, and approves small ones.",
    permissions: ["orders.read", "orders.create", "orders.approve"],
  },
  reason: "Buyers approve orders under 500 euros from November, as finance agreed.",
  requested_by: "01a1a000-0000-7000-8000-0000000000p1",
  requester_name: "Ravi Ravuri",
  requested_at: ago(40),
  status: "pending",
  expires_at: later(7),
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

const changes = [
  change,
  {
    ...change,
    id: "01a1a000-0000-7000-8000-0000000000c2",
    area: "mfa_and_password_policies",
    kind: "settings.value.set",
    title: "Set identity.password.min_length for the organisation",
    before: { value: 12 },
    after: { value: 14 },
    requested_at: ago(180),
    expires_at: later(6),
  },
  {
    ...change,
    id: "01a1a000-0000-7000-8000-0000000000c3",
    area: "single_sign_on",
    kind: "identity.sso.update",
    title: "Change the company login Contoso",
    status: "applied",
    decided_by: me.id,
    decider_name: "Admin",
    decided_at: ago(60 * 24),
    decision_reason: "Matches the provider's new address.",
    requested_at: ago(60 * 26),
  },
];

const pack = {
  id: "01a1a000-0000-7000-8000-0000000000e1",
  period_from: `${utcDay(-31)}T00:00:00Z`,
  period_to: `${utcDay(-1)}T00:00:00Z`,
  requested_by: me.id,
  requester_name: "Admin",
  requested_at: ago(12),
  status: "ready",
  bytes: 86_311,
  sha256: "6e".repeat(32),
  manifest_sha256: "a9".repeat(32),
  chain_segment: 4,
  chain_position: 18_204,
  chain_hash: "3c".repeat(32),
  anchor_id: "01a1a000-0000-7000-8000-0000000000f1",
  made_at: ago(11),
  failure: null,
};

/** A tenant administrator with the console's stubs, the pending changes, the evidence packs and the validation page. */
async function stubGovernance(page: Page, release = { version: "2026.10.23.57", development: false }): Promise<void> {
  await stubSettings(page);
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
  await page.route("**/api/v1/me/context", (route) => route.fulfill({ json: { ...context, release } }));
  await page.route("**/api/v1/me/access?**", (route) => route.fulfill({ json: { scope: "unit", permissions } }));
  await page.route("**/api/v1/changes?**", (route) => {
    const view = new URL(route.request().url()).searchParams.get("view");
    const rows = view === "mine" ? [changes[2]] : view === "all" ? changes : changes.slice(0, 2);
    return route.fulfill({ json: envelope(route.request().url(), rows) });
  });
  for (const row of changes) {
    await page.route(`**/api/v1/changes/${row.id}`, (route) => route.fulfill({ json: row }));
  }
  await page.route("**/api/v1/evidence-packs?**", (route) =>
    route.fulfill({
      json: envelope(route.request().url(), [
        pack,
        {
          ...pack,
          id: "01a1a000-0000-7000-8000-0000000000e2",
          status: "building",
          bytes: null,
          requested_at: ago(1),
          period_from: `${utcDay(-366)}T00:00:00Z`,
        },
      ]),
    }),
  );
  await page.route(`**/api/v1/evidence-packs/${pack.id}`, (route) => route.fulfill({ json: pack }));
  await page.route("**/api/v1/validation", (route) =>
    route.fulfill({
      json: {
        version: release.version,
        development: release.development,
        pack_available: true,
        pack_bytes: 2_431_118,
      },
    }),
  );
}

for (const scheme of schemes) {
  test(`the pending changes and one change with its before and after side by side, and the approval with its reason, in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-068 AC1, TF-068 AC3, TF-172 AC1, ADR-0024, ADR-0052]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubGovernance(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/console/changes");
    await expect(page.getByRole("gridcell", { name: "Change the role Buyer" })).toBeVisible();
    await expect(page.getByRole("gridcell", { name: "MFA and password policies" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/changes-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.goto(`/console/changes/${change.id}`);
    const compare = page.getByRole("table", { name: /before the change and after it/ });
    await expect(compare.locator("[data-field=permissions]")).toContainText("Changed");
    await expect(compare.locator("[data-field=permissions]")).toContainText("orders.approve");
    await page.screenshot({ path: `test-results/a11y/change-record-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Approve" }).click();
    const dialog = page.getByRole("dialog", { name: "Approve the change" });
    await dialog.getByRole("button", { name: "Approve" }).click();
    await expect(dialog.getByText("Say why: the reason is kept in the audit trail.")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/change-approve-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`the evidence packs, the form for a period with its errors, and a ready pack with its hashes and the chain head, in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-067 AC1, TF-172 AC1, ADR-0024, ADR-0052]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubGovernance(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/compliance/evidence-packs");
    await expect(page.getByRole("gridcell", { name: "Ready", exact: true })).toBeVisible();
    await expect(page.getByRole("gridcell", { name: "Being made" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/evidence-packs-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Make a pack" }).click();
    const form = page.getByRole("dialog", { name: "Make an evidence pack" });
    await form.getByLabel("From", { exact: true }).fill(utcDay(-1));
    await form.getByLabel("Up to and including", { exact: true }).fill(utcDay(-10));
    await form.getByRole("button", { name: "Make the pack" }).click();
    await expect(form.getByText("Choose a last day on or after the first.")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/evidence-pack-form-${scheme}.png` });
    await expectNoViolation(page);

    await page.goto(`/compliance/evidence-packs/${pack.id}`);
    await expect(page.locator("[data-chain-head]")).toContainText("Segment 4, position 18204");
    await expect(page.getByRole("button", { name: "Download" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/evidence-pack-record-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`the validation page with the installed version and its pack, and the version in help, in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-218 AC3, TF-172 AC1, ADR-0024, ADR-0052]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubGovernance(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/compliance/validation");
    await expect(page.locator("[data-version]").first()).toHaveText("2026.10.23.57");
    await expect(page.getByRole("link", { name: "Download the pack" })).toHaveAttribute(
      "href",
      "/api/v1/validation/pack",
    );
    await page.screenshot({ path: `test-results/a11y/validation-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Help and keyboard shortcuts" }).click();
    await expect(page.getByRole("dialog", { name: "Help" })).toContainText("Version 2026.10.23.57");
    await page.screenshot({ path: `test-results/a11y/help-version-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`the four-eyes settings chosen by their areas, and a change held for approval in the information colour, in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-068 AC1, TF-172 AC1, ADR-0024, ADR-0052]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubGovernance(page);
    const areas = {
      ...tenantSettings.data[0],
      key: "changes.four_eyes.areas",
      module: "changes",
      type: "json",
      description: "The areas of sensitive configuration whose changes wait for a second administrator.",
      requirement: "A list of areas, each once.",
      minimum: null,
      maximum: null,
      whole_number: false,
      scopes: ["tenant"],
      default: [],
      value: ["roles", "single_sign_on", "mfa_and_password_policies"],
    };
    await page.route("**/api/v1/settings?**", (route) =>
      route.fulfill({ json: { ...tenantSettings, data: [...tenantSettings.data, areas] } }),
    );
    await page.route("**/api/v1/settings/changes.four_eyes.areas", (route) =>
      route.fulfill({
        status: 202,
        contentType: "application/problem+json",
        body: JSON.stringify({
          status: 202,
          title: "The change waits for a second administrator's approval.",
          error_code: "changes.change.held",
          change_id: changes[1].id,
          correlation_id: "c-held",
        }),
      }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/console/settings");
    const setting = page.getByRole("article", { name: "Changes that wait for a second administrator" });
    await expect(setting).toContainText("Roles and their assignments, Single sign-on, MFA and password policies");
    await setting.getByRole("button", { name: "Change: Changes that wait for a second administrator" }).click();
    const dialog = page.getByRole("dialog", { name: "Change: Changes that wait for a second administrator" });
    await expect(dialog.getByRole("combobox", { name: "Areas" })).toBeVisible();
    await dialog.getByRole("button", { name: "Save" }).click();
    await expect(dialog.getByRole("status")).toContainText("Your change waits for a second administrator's approval.");
    await page.screenshot({ path: `test-results/a11y/four-eyes-held-${scheme}.png` });
    await expectNoViolation(page);
  });
}
