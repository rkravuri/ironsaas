import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { administrator, envelope, me, record, stubAccess, stubSettings, tenantSettings, units } from "./stubs";
import { reviewId, stubReviews } from "./stubs-delegation";

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
const day = (offsetDays: number) => new Date(Date.now() + offsetDays * 86_400_000).toISOString().slice(0, 10);
const monthStart = (back: number) => {
  const now = new Date();
  return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - back, 1)).toISOString().slice(0, 10);
};

const signature = (overrides: Record<string, unknown> = {}) => ({
  id: "01a19000-0000-7000-8000-000000000001",
  record_type: "audit_trail_review",
  record_id: "01a19000-0000-7000-8000-0000000000a1",
  record_version: 1,
  signer_id: me.id,
  signer_name: "Rita Reviewer",
  signer_login_id: "rita@acme.localhost",
  method: "password_and_app",
  meaning: "reviewed",
  comment: null,
  signed_at: ago(90),
  snapshot_hash: "4f".repeat(32),
  hash_matches: true,
  current_version: 1,
  record_changed: false,
  ...overrides,
});

const review = {
  id: "01a19000-0000-7000-8000-0000000000a1",
  period_from: monthStart(1),
  period_to: day(-1),
  outcome: "findings",
  reviewer_id: me.id,
  reviewer_name: "Rita Reviewer",
  recorded_at: ago(90),
  unit_ids: [units[3].id],
  event_types: ["identity.login_failed", "authz.role_assigned"],
  notes: "Two failed sign-ins from one address in a night; the person had mistyped, and changed their password.",
  searches: [
    { label: "Failed sign-ins last month", link: "/audit?event_type=identity.login_failed" },
    { label: "Roles assigned last month", link: "/audit?event_type=authz.role_assigned" },
  ],
  signature: signature(),
};

const challenge = (first: boolean) => ({
  first_in_session: first,
  login_id: first,
  password: true,
  second_factor: first,
  methods: first ? ["authenticator_app", "recovery_code"] : [],
  passkey_options: null,
  provider_id: null,
  provider_name: null,
  provider_confirmed: false,
  reauthenticate: true,
  policy: { required: true, meanings: ["reviewed", "approved", "rejected"], reauthenticate: true },
});

const signableTypes = (decisionsSigned: boolean) => ({
  data: [
    {
      record_type: "access_review_decision",
      name: "Access review decision",
      meanings: ["approved", "rejected"],
      always_signed: false,
      policy: { required: decisionsSigned, meanings: ["approved", "rejected"], reauthenticate: true },
      default: { required: false, meanings: ["approved", "rejected"], reauthenticate: true },
    },
    {
      record_type: "audit_trail_review",
      name: "Audit-trail review",
      meanings: ["reviewed"],
      always_signed: true,
      policy: { required: true, meanings: ["reviewed"], reauthenticate: true },
      default: { required: true, meanings: ["reviewed"], reauthenticate: true },
    },
  ],
});

/** A reviewer of the audit trail with the console's stubs, the reviews, their months, and the signing endpoints. */
async function stubCompliance(page: Page): Promise<void> {
  await stubAccess(page);
  const permissions = [
    ...administrator,
    "audit.events.read",
    "compliance.audit_reviews.read",
    "compliance.audit_reviews.record",
  ];
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
  await page.route("**/api/v1/role-assignments/mine", (route) => route.fulfill({ json: { data: [] } }));
  await page.route("**/api/v1/delegations/mine", (route) => route.fulfill({ json: { given: [], received: [] } }));
  await page.route("**/api/v1/organisation-units", (route) => route.fulfill({ json: { data: units } }));
  await page.route("**/api/v1/audit-events/types", (route) =>
    route.fulfill({
      json: {
        data: [
          { code: "authz.role_assigned", name: "Role assigned" },
          { code: "identity.login_failed", name: "Sign-in failed" },
          { code: "record.changed", name: "Record changed" },
        ],
      },
    }),
  );
  await page.route("**/api/v1/audit-reviews/coverage?**", (route) =>
    route.fulfill({
      json: {
        months: Array.from({ length: 12 }, (_, back) => ({
          from: monthStart(back),
          to: monthStart(back),
          reviews: back === 1 || back === 2 || back === 4 ? [review.id] : [],
          outcomes: back === 1 ? ["findings"] : back === 2 || back === 4 ? ["no_findings"] : [],
        })),
      },
    }),
  );
  const rows = [1, 2, 4].map((back, index) => ({
    id: index === 0 ? review.id : `01a19000-0000-7000-8000-0000000000a${index + 1}`,
    period_from: monthStart(back),
    period_to: monthStart(back - 1),
    outcome: back === 1 ? "findings" : "no_findings",
    reviewer_id: me.id,
    reviewer_name: index === 2 ? "Grace Hopper" : "Rita Reviewer",
    recorded_at: ago(60 * 24 * 30 * back - 60),
  }));
  await page.route("**/api/v1/audit-reviews?**", (route) =>
    route.fulfill({ json: envelope(route.request().url(), rows) }),
  );
  await page.route(`**/api/v1/audit-reviews/${review.id}`, (route) => route.fulfill({ json: review }));
  await page.route("**/api/v1/signatures?**", (route) => route.fulfill({ json: { data: [review.signature] } }));
  await page.route("**/api/v1/signatures/challenge?**", (route) => route.fulfill({ json: challenge(true) }));
  await page.route("**/api/v1/signatures/types", (route) => route.fulfill({ json: signableTypes(true) }));
  await page.route("**/api/v1/audit-events?**", (route) =>
    route.fulfill({ json: envelope(route.request().url(), []) }),
  );
}

for (const scheme of schemes) {
  test(`the audit-trail reviews in the ${scheme} scheme: the months with the ones nobody reviewed, the reviews, the form with its errors and the signing dialog, and no WCAG 2.1 AA violation [TF-219 AC3, TF-063 AC4, TF-172 AC1, ADR-0024, ADR-0051]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubCompliance(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/compliance/audit-reviews");
    const months = page.getByRole("region", { name: "The last twelve months" });
    await expect(months.locator("[data-reviewed=false]")).toHaveCount(9);
    await expect(months.locator("[data-reviewed=true]")).toHaveCount(3);
    await expect(page.getByRole("gridcell", { name: "Findings", exact: true })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/audit-reviews-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Record a review" }).click();
    const form = page.getByRole("dialog", { name: "Record a review of the audit trail" });
    await form.getByLabel("From", { exact: true }).fill(monthStart(0));
    await form.getByLabel("To", { exact: true }).fill(day(-1) < monthStart(0) ? monthStart(0) : day(-1));
    await form
      .locator("label")
      .filter({ hasText: /^Findings$/ })
      .click();
    await form.getByRole("button", { name: "Add a search" }).click();
    await form.getByRole("button", { name: "Continue to sign" }).click();
    await expect(form.getByText("Say what you found: findings need notes.")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/audit-review-form-${scheme}.png` });
    await expectNoViolation(page);

    await form.getByLabel(/^Notes/).fill("Nothing out of the ordinary beyond one mistyped password.");
    await form.getByLabel(/what it looked for/).fill("Failed sign-ins this month");
    await form.getByLabel(/Search 1: address/).fill("/audit?event_type=identity.login_failed");
    await form.getByRole("button", { name: "Continue to sign" }).click();
    const signing = page.getByRole("dialog", { name: "Sign the review" });
    await expect(signing.getByLabel(/^Login identifier/)).toBeVisible();
    await expect(signing.getByLabel(/^Code from your authenticator app/)).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/signing-dialog-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`an audit-trail review's record with its signature block, on screen and in print, in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-219 AC1, TF-219 AC3, TF-065 AC2, TF-172 AC1, ADR-0024, ADR-0051]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubCompliance(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto(`/compliance/audit-reviews/${review.id}`);
    const block = page.getByRole("region", { name: "Signatures" });
    await expect(block).toContainText("Rita Reviewer");
    await expect(block).toContainText("GMT+5:30");
    await expect(block).toContainText("Reviewed");
    await expect(page.getByRole("link", { name: "Failed sign-ins last month" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/audit-review-record-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.emulateMedia({ media: "print", colorScheme: scheme });
    await expect(page.getByRole("navigation", { name: "Main navigation" })).toBeHidden();
    await expect(page.getByRole("tablist")).toBeHidden();
    await expect(block).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/audit-review-print-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`a signature against an earlier version and the snapshot it signed in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-063 AC2, TF-063 AC3, TF-172 AC1, ADR-0024, ADR-0051]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubCompliance(page);
    const changed = signature({
      id: "01a19000-0000-7000-8000-000000000002",
      record_type: "sample_document",
      meaning: "approved",
      signer_name: "Ada Lovelace",
      comment: "Agreed with finance.",
      record_changed: true,
      current_version: 3,
    });
    await page.route(`**/api/v1/signatures/${changed.id}/snapshot`, (route) =>
      route.fulfill({
        json: {
          signature: changed,
          type_name: "Supply agreement",
          canonical: `["iron-snapshot-v1","sample_document","${changed.record_id}",1,{"amount":1200.5,"bank_account":"****3000","body":"Ten valves a month."}]`,
          form: 1,
          recomputed_hash: changed.snapshot_hash,
          content: { amount: 1200.5, bank_account: "****3000", body: "Ten valves a month." },
        },
      }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto(`/signatures/${changed.id}`);
    await expect(page.getByRole("heading", { name: "What was signed: Supply agreement" })).toBeVisible();
    await expect(page.locator("[data-record-changed]")).toContainText("The record has changed since this signature");
    await page.screenshot({ path: `test-results/a11y/signed-snapshot-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("tab", { name: "Canonical form" }).click();
    await expect(page.locator("[data-canonical]")).toContainText("iron-snapshot-v1");
    await page.screenshot({ path: `test-results/a11y/signed-snapshot-form-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`a record's History tab with a save as its fields, masked and secret, in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-062 AC1, TF-062 AC3, TF-064 AC4, TF-172 AC1, ADR-0024, ADR-0051]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubCompliance(page);
    const saved = {
      id: "01a19000-0000-7000-8000-0000000000e1",
      event_type: "record.changed",
      event_name: "Record changed",
      outcome: "success",
      actor_id: me.id,
      actor_name: "Admin",
      source: "ui",
      target: null,
      record_type: "principal",
      record_id: record.id,
      reason: null,
      correlation_id: "c-1",
      details: { version: "4" },
      created_at: ago(5),
      created_by: me.id,
      severity: "normal",
      chain_position: 1301,
      changes: [
        { field: "display_name", old: "Ada L", new: "Ada Lovelace", treatment: "plain" },
        { field: "email", old: "ada@old.example", new: "ada@acme.localhost", treatment: "plain" },
        { field: "password_hash", old: null, new: null, treatment: "secret" },
      ],
    };
    const deactivated = {
      ...saved,
      id: "01a19000-0000-7000-8000-0000000000e2",
      event_type: "identity.user_deactivated",
      event_name: "Person deactivated",
      changes: null,
      target: record.display_name,
      reason: "Left the company",
      created_at: ago(60),
    };
    await page.route("**/api/v1/audit-events?**", (route) =>
      route.fulfill({ json: envelope(route.request().url(), [saved, deactivated]) }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto(`/users/${record.id}`);
    await page.getByRole("tab", { name: "History" }).click();
    const history = page.locator("[data-panel=history]");
    await expect(history.getByRole("table", { name: "3 fields changed" })).toContainText("Ada Lovelace");
    await expect(history).toContainText("Person deactivated");
    await expect(history).toContainText("Changed. A secret value is never kept.");
    await page.screenshot({ path: `test-results/a11y/history-changes-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`the signing policy in Settings in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-063 AC1, TF-172 AC1, ADR-0024, ADR-0051]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSettings(page);
    const policies = {
      ...tenantSettings.data[0],
      key: "compliance.signing.policies",
      module: "compliance",
      type: "json",
      description: "The signing policy per document type.",
      requirement: "An object keyed by document type.",
      minimum: null,
      maximum: null,
      whole_number: false,
      default: {},
      value: { access_review_decision: { required: true, meanings: ["approved", "rejected"], reauthenticate: true } },
    };
    await page.route("**/api/v1/settings?**", (route) =>
      route.fulfill({ json: { ...tenantSettings, data: [...tenantSettings.data, policies] } }),
    );
    await page.route("**/api/v1/signatures/types", (route) => route.fulfill({ json: signableTypes(true) }));
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/console/settings");
    const setting = page.getByRole("article", { name: "Signing policy" });
    await expect(setting).toContainText("Access review decisions: signature required, password asked.");
    await setting.getByRole("button", { name: "Change: Signing policy" }).click();
    const dialog = page.getByRole("dialog", { name: "Change: Signing policy" });
    await expect(dialog.getByRole("group", { name: "Audit-trail review" })).toContainText(
      "Always signed, with the password",
    );
    await page.screenshot({ path: `test-results/a11y/signing-policy-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`an access review whose decisions are signed, and the signing dialog for a revoke, in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-063 AC1, TF-172 AC1, ADR-0024, ADR-0051]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubReviews(page);
    await page.route("**/api/v1/signatures/types", (route) => route.fulfill({ json: signableTypes(true) }));
    await page.route("**/api/v1/signatures/challenge?**", (route) => route.fulfill({ json: challenge(false) }));
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto(`/console/access-reviews/${reviewId}`);
    await expect(page.locator("[data-signed-decisions]")).toBeVisible();
    await expect(page.getByRole("button", { name: "Confirm the 4 items on this page" })).toBeDisabled();
    await page.getByRole("gridcell", { name: "Marie Curie" }).click();
    await page.getByRole("button", { name: "Revoke" }).click();
    const dialog = page.getByRole("dialog", { name: "Sign the revocation" });
    await expect(dialog).toContainText("Marie Curie loses Store manager at");
    await expect(dialog).toContainText("Rejected");
    await page.screenshot({ path: `test-results/a11y/review-sign-revoke-${scheme}.png` });
    await expectNoViolation(page);
  });
}
