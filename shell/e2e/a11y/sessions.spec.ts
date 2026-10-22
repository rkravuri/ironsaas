import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { administrator, envelope, me, record, stubAccess, units } from "./stubs";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

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

const sessions = [
  {
    id: "01a17000-0000-7000-8000-000000000001",
    principal_id: me.id,
    login_id: me.login_id,
    display_name: "Admin",
    browser: "Chrome",
    platform: "Windows",
    city: "London",
    country: "United Kingdom",
    method: "password",
    started_at: ago(40),
    last_activity_at: ago(1),
    impersonator_name: null,
    current: true,
  },
  {
    id: "01a17000-0000-7000-8000-000000000002",
    principal_id: me.id,
    login_id: me.login_id,
    display_name: "Admin",
    browser: "Safari",
    platform: "iOS",
    city: null,
    country: "Sweden",
    method: "sso",
    started_at: ago(300),
    last_activity_at: ago(120),
    impersonator_name: null,
    current: false,
  },
  {
    id: "01a17000-0000-7000-8000-000000000003",
    principal_id: record.id,
    login_id: record.login_id,
    display_name: record.display_name,
    browser: "Firefox",
    platform: "Linux",
    city: "Berlin",
    country: "Germany",
    method: "impersonation",
    started_at: ago(20),
    last_activity_at: ago(2),
    impersonator_name: "Admin",
    current: false,
  },
];

const summary = {
  signed_in_now: 12,
  days: Array.from({ length: 14 }, (_, index) => ({
    date: new Date(Date.now() - (13 - index) * 86_400_000).toISOString().slice(0, 10),
    started: 20 + index,
    peak_people: 8 + (index % 4),
    ended_by_administrator: index % 5 === 0 ? 1 : 0,
  })),
};

const documents = {
  documents: [
    {
      source: "platform",
      key: "terms_of_service",
      version: 3,
      title: "Terms of service",
      body: "The platform's terms of service.",
      published_at: "2026-10-20T09:00:00Z",
      accepted: 41,
    },
    {
      source: "tenant",
      key: "code-of-conduct",
      version: 2,
      title: "Code of conduct",
      body: "Be kind to each other and to the data.",
      published_at: "2026-10-21T09:00:00Z",
      accepted: 7,
    },
  ],
};

const acceptances = Array.from({ length: 6 }, (_, index) => ({
  id: `01a17100-0000-7000-8000-${String(index + 1).padStart(12, "0")}`,
  principal_id: `01a14e12-0000-7000-8000-${String(index + 1).padStart(12, "0")}`,
  login_id: `user${index + 1}@acme.localhost`,
  display_name: `Person ${index + 1}`,
  source: index % 2 === 0 ? "platform" : "tenant",
  document_key: index % 2 === 0 ? "terms_of_service" : "code-of-conduct",
  version: index % 2 === 0 ? 3 : 2,
  accepted_at: ago(60 * (index + 1)),
}));

/** An administrator who may manage people, sessions, documents and impersonation, with the console's stubs. */
async function stubSessions(page: Page, extra: Record<string, unknown> = {}): Promise<void> {
  await stubAccess(page);
  const permissions = [
    ...administrator,
    "identity.sessions.manage",
    "identity.documents.manage",
    "identity.impersonation.start",
  ];
  await page.route("**/api/v1/me", (route) =>
    route.fulfill({
      json: {
        ...me,
        permissions,
        permissions_anywhere: permissions,
        home_unit: { id: units[0].id, name: units[0].name, code: units[0].code },
        ...extra,
      },
    }),
  );
  await page.route("**/api/v1/me/access?**", (route) => route.fulfill({ json: { scope: "unit", permissions } }));
  await page.route("**/api/v1/sessions/summary?**", (route) => route.fulfill({ json: summary }));
  await page.route("**/api/v1/sessions?**", (route) =>
    route.fulfill({ json: envelope(route.request().url(), sessions) }),
  );
  await page.route("**/api/v1/me/sessions?**", (route) =>
    route.fulfill({ json: envelope(route.request().url(), sessions.slice(0, 2)) }),
  );
  await page.route("**/api/v1/documents", (route) => route.fulfill({ json: documents }));
  await page.route("**/api/v1/documents/acceptances?**", (route) =>
    route.fulfill({ json: envelope(route.request().url(), acceptances) }),
  );
  await page.route("**/api/v1/role-assignments/mine", (route) => route.fulfill({ json: { data: [] } }));
  await page.route("**/api/v1/delegations/mine", (route) => route.fulfill({ json: { given: [], received: [] } }));
  await page.route(`**/api/v1/users/${record.id}`, (route) =>
    route.fulfill({ json: { ...record, factors: 1, takeover_allowed: true, emergency: true } }),
  );
}

for (const scheme of schemes) {
  test(`the sessions console in the ${scheme} scheme: the summary, the live sessions, ending one with a reason and revoking everything, with no WCAG 2.1 AA violation [TF-022 AC5, TF-029 AC1, TF-172 AC1, ADR-0024, ADR-0049]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSessions(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/console/sessions");
    await expect(page.getByText("People signed in now")).toBeVisible();
    const grid = page.getByRole("region", { name: "Live sessions" });
    await expect(grid.locator('.ag-row[row-index="0"]').first()).toContainText("Chrome on Windows");
    await page.screenshot({ path: `test-results/a11y/sessions-console-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await grid.locator('.ag-row[row-index="1"]').first().click();
    await page.getByRole("button", { name: "End selected session" }).click();
    const end = page.getByRole("dialog", { name: /^End .*'s session\?$/ });
    await expect(end.getByLabel(/^Reason/)).toBeFocused();
    await page.screenshot({ path: `test-results/a11y/sessions-end-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");

    await page.getByRole("button", { name: "Revoke every session and token" }).click();
    const revoke = page.getByRole("dialog", { name: "Revoke every session and token?" });
    await expect(revoke).toContainText("yours too");
    await page.screenshot({ path: `test-results/a11y/sessions-revoke-tenant-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`my sessions in the ${scheme} scheme: this one and the others, and signing out everywhere else, with no WCAG 2.1 AA violation [TF-022 AC4, TF-172 AC1, ADR-0024, ADR-0049]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSessions(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/me?tab=sessions");
    const table = page.getByRole("table", { name: /Your live sessions/ });
    await expect(table).toContainText("Safari on iOS");
    await page.screenshot({ path: `test-results/a11y/profile-sessions-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Sign out everywhere else" }).click();
    await expect(page.getByRole("dialog", { name: "Sign out everywhere else?" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/profile-sessions-end-others-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`the user record's Sign in as, Revoke everything and emergency mark in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-025 AC1, TF-029 AC4, TF-172 AC1, ADR-0024, ADR-0049]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSessions(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto(`/users/${record.id}`);
    await expect(page.getByText(/Emergency account/)).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/user-access-actions-${scheme}.png` });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Sign in as" }).click();
    const impersonate = page.getByRole("dialog", { name: `Sign in as ${record.display_name}?` });
    await expect(impersonate.getByLabel(/^Reason/)).toBeFocused();
    await page.screenshot({ path: `test-results/a11y/user-impersonate-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");

    await page.getByRole("button", { name: "Revoke everything" }).click();
    await expect(page.getByRole("dialog", { name: `Revoke everything of ${record.display_name}?` })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/user-revoke-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`the impersonation band and an emergency sign-in's alert in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-025 AC2, TF-172 AC1, ADR-0024, ADR-0049]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSessions(page, {
      display_name: "Pat Person",
      impersonation: {
        impersonator_name: "Ada Admin",
        until: new Date(Date.now() + 90 * 60_000).toISOString(),
        reason: "Their invoices do not show",
        operator: false,
      },
    });
    await page.route("**/api/v1/security-alerts", (route) =>
      route.fulfill({
        json: {
          emergency_sign_ins: [
            {
              id: "e1",
              principal_id: record.id,
              display_name: "Erin Emergency",
              login_id: "erin@acme.localhost",
              at: ago(15),
            },
          ],
        },
      }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/users");
    await expect(page.getByText("You are signed in as Pat Person")).toBeVisible();
    await expect(page.getByText("An emergency account signed in")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/impersonation-band-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`the documents console and publishing a version in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-028 AC2, TF-172 AC1, ADR-0024, ADR-0049]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSessions(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/console/documents");
    await expect(page.getByRole("table", { name: /The current version of each document/ })).toContainText(
      "Code of conduct",
    );
    const grid = page.getByRole("region", { name: "Who accepted" });
    await expect(grid.locator('.ag-row[row-index="0"]').first()).toContainText("Person 1");
    await page.screenshot({ path: `test-results/a11y/documents-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Publish the next version of Code of conduct" }).click();
    await expect(page.getByRole("dialog", { name: "Publish the next version of Code of conduct" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/documents-publish-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`the terms page, declining it, and a sign-in link that does not work in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-028 AC1, TF-028 AC4, TF-172 AC1, ADR-0024, ADR-0049]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSessions(page, { terms_acceptance_required: true });
    await page.route("**/api/v1/me/terms", (route) =>
      route.fulfill({
        json: {
          pending: [
            { ...documents.documents[0], accepted: undefined },
            { ...documents.documents[1], accepted: undefined },
          ],
        },
      }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/users");
    await expect(page.getByRole("heading", { name: "Read and accept" })).toBeVisible();
    await expect(page.getByRole("article", { name: "Code of conduct" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/terms-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Decline and sign out" }).click();
    await expect(page.getByRole("dialog", { name: "Decline and sign out?" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/terms-decline-${scheme}.png` });
    await expectNoViolation(page);

    await page.unrouteAll({ behavior: "ignoreErrors" });
    await page.route("**/api/**", (route) =>
      route.fulfill({
        status: 401,
        contentType: "application/problem+json",
        body: JSON.stringify({ status: 401, error_code: "auth.not_authenticated" }),
      }),
    );
    await page.route("**/auth/sign-in-options", (route) =>
      route.fulfill({ json: { passkey_sign_in: false, providers: [] } }),
    );
    await page.route("**/auth/sign-in-link", (route) =>
      route.fulfill({
        status: 404,
        contentType: "application/problem+json",
        body: JSON.stringify({ status: 404, error_code: "identity.sign_in_link.invalid", correlation_id: "0a1b2c3d" }),
      }),
    );
    await page.goto("/sign-in-link#used-code");
    await expect(page.getByRole("alert")).toContainText("This sign-in link does not work");
    await page.screenshot({ path: `test-results/a11y/sign-in-link-${scheme}.png` });
    await expectNoViolation(page);
  });
}
