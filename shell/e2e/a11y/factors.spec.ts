import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { envelope, me, record, roles, stubAccess, stubPasswords, stubSettings, tenantSettings } from "./stubs";

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

const inFiveMinutes = () => new Date(Date.now() + 5 * 60_000).toISOString();
const codes = [
  "K7QD-2MXA-PL4R-9TZE",
  "B3NW-QF8H-JX2C-7YUD",
  "R6TP-ZA4M-WC9Q-3EHN",
  "F2LX-8JUB-NQ5D-KV7M",
  "H9CW-3RPE-TA6Z-YB2Q",
  "M4ZD-7KGN-XV3P-WQ8F",
  "T8BJ-5PXC-DM2Q-LH6R",
  "W3VA-9NQZ-BK7E-PJ4C",
  "Q5HR-2YDM-FX8W-CN3T",
  "Z7PK-4WBA-HM6R-EV9X",
];

/** Nobody signed in: /me answers 401, the tenant offers a passkey alone, and the login answers the step a test names. */
async function signingIn(page: Page, step: { kind: string; methods: string[] }): Promise<void> {
  await page.route("**/api/**", (route) =>
    route.fulfill({
      status: 401,
      contentType: "application/problem+json",
      body: JSON.stringify({ status: 401, error_code: "auth.not_authenticated" }),
    }),
  );
  await page.route("**/auth/sign-in-options", (route) => route.fulfill({ json: { passkey_sign_in: true } }));
  await page.route("**/auth/login", (route) =>
    route.fulfill({ json: { step: { ...step, expires_at: inFiveMinutes() } } }),
  );
}

async function typePassword(page: Page): Promise<void> {
  await page.getByRole("textbox", { name: /Login identifier/ }).fill("ada@acme.localhost");
  await page.getByLabel(/^Password/).fill("correct horse battery staple");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
}

const factors = {
  factors: [
    {
      id: "f1",
      kind: "authenticator_app",
      name: "My phone",
      created_at: "2026-10-01T09:00:00Z",
      last_used_at: "2026-10-21T08:30:00Z",
    },
    { id: "f2", kind: "passkey", name: "Work laptop", created_at: "2026-10-02T09:00:00Z", last_used_at: null },
  ],
  recovery_codes_left: 8,
  required: true,
  allowed: ["authenticator_app", "passkey"],
  enrol_by: null,
};

for (const scheme of schemes) {
  test(`the second step of a sign-in in the ${scheme} scheme: passkey, code and recovery code, and the cooling period in words, with no WCAG 2.1 AA violation [TF-172 AC1, TF-013 AC2, ADR-0047]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await signingIn(page, { kind: "second_factor", methods: ["passkey", "authenticator_app", "recovery_code"] });
    await page.route("**/auth/second-factor", (route) =>
      route.fulfill({
        status: 401,
        contentType: "application/problem+json",
        body: JSON.stringify({
          status: 401,
          error_code: "identity.second_factor.cooling",
          retry_after_seconds: 60,
          correlation_id: "corr-cool",
        }),
      }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/login");

    await expect(page.getByRole("button", { name: "Sign in with a passkey" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/login-passkey-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await typePassword(page);
    await expect(page.getByRole("heading", { name: "Confirm it is you" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Use your passkey" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/login-second-factor-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByLabel(/Code from your authenticator app/).fill("000000");
    await page.getByRole("button", { name: "Continue" }).click();
    await expect(page.getByRole("alert")).toContainText("Wait 60 seconds, then try again.");
    await page.screenshot({ path: `test-results/a11y/login-cooling-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`enrolment at sign-in in the ${scheme} scheme: the choice, the authenticator app with its QR code and key, and the recovery codes shown once, with no WCAG 2.1 AA violation [TF-172 AC1, TF-013 AC1, ADR-0047]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 1000 });
    await signingIn(page, { kind: "enrolment", methods: ["authenticator_app", "passkey"] });
    await page.route("**/auth/enrolment/authenticator-app", (route) =>
      route.fulfill({
        json: {
          secret: "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP",
          uri: "otpauth://totp/IronSaaS%20(acme.localhost):ada%40acme.localhost?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=IronSaaS&algorithm=SHA1&digits=6&period=30",
          expires_at: inFiveMinutes(),
        },
      }),
    );
    await page.route("**/auth/enrolment/authenticator-app/confirm", (route) =>
      route.fulfill({ json: { principal: {}, session: {}, recovery_codes: codes } }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/login");

    await typePassword(page);
    await expect(page.getByRole("heading", { name: "Set up a second factor" })).toBeVisible();
    await expect(page.getByText("Choose how you will confirm it is you")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/login-enrolment-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByText("Authenticator app", { exact: true }).click();
    await expect(page.getByRole("img", { name: "QR code for your authenticator app" })).toBeVisible();
    await expect(page.getByRole("textbox", { name: "Key to type into the app" })).toHaveValue(
      "JBSW Y3DP EHPK 3PXP JBSW Y3DP EHPK 3PXP",
    );
    await page.screenshot({ path: `test-results/a11y/login-enrol-app-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByLabel(/Code the app shows now/).fill("123456");
    await page.getByRole("button", { name: "Save the authenticator app" }).click();
    await expect(page.getByRole("heading", { name: "Save your recovery codes" })).toBeVisible();
    await expect(page.getByRole("list", { name: "Your recovery codes" }).getByRole("listitem")).toHaveCount(10);
    await expect(page.getByRole("button", { name: "Continue" })).toBeDisabled();
    await page.screenshot({ path: `test-results/a11y/login-recovery-codes-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`My profile's second factors in the ${scheme} scheme: each factor in words, the recovery codes left, and the add dialog, with no WCAG 2.1 AA violation [TF-172 AC1, ADR-0047]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 1100 });
    await stubAccess(page);
    await stubPasswords(page);
    await page.route("**/api/v1/me/factors", (route) => route.fulfill({ json: factors }));
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/me?tab=security");

    const table = page.getByRole("table", { name: "Your second factors" });
    await expect(table).toContainText("Work laptop");
    await expect(page.getByText("You have 8 recovery codes left.")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/profile-factors-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Add a second factor" }).click();
    const dialog = page.getByRole("dialog", { name: "Add a second factor" });
    await dialog.getByText("Passkey", { exact: true }).click();
    await expect(dialog.getByRole("button", { name: "Create a passkey" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/profile-add-passkey-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");

    await page.getByRole("button", { name: "Rename Work laptop" }).click();
    const rename = page.getByRole("dialog", { name: "Rename Work laptop" });
    await expect(rename.getByRole("textbox", { name: "Name" })).toHaveValue("Work laptop");
    await page.screenshot({ path: `test-results/a11y/profile-rename-factor-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`the re-authentication dialog in the ${scheme} scheme opens on a high-risk action after an old sign-in, with no WCAG 2.1 AA violation [TF-172 AC1, TF-248 AC5, ADR-0047]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubAccess(page);
    await page.route(`**/api/v1/users/${record.id}/recovery-link`, (route) =>
      route.fulfill({
        status: 403,
        contentType: "application/problem+json",
        body: JSON.stringify({ status: 403, error_code: "auth.reauthentication_required" }),
      }),
    );
    await page.route("**/api/v1/me/reauthentication", (route) =>
      route.fulfill({
        json: { second_factor: true, methods: ["authenticator_app", "recovery_code"], passkey_options: null },
      }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto(`/users/${record.id}`);

    await page.locator("[data-panel=record-header]").getByRole("button", { name: "Issue a recovery link" }).click();
    await page
      .getByRole("dialog", { name: `Issue a recovery link for ${record.display_name}?` })
      .getByRole("button", { name: "Issue the link" })
      .click();
    const dialog = page.getByRole("dialog", { name: "Confirm it is you" });
    await expect(dialog.getByLabel(/^Password/)).toBeFocused();
    await expect(dialog.getByLabel(/Code from your authenticator app/)).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/reauthentication-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`resetting a person's factors in the ${scheme} scheme asks a reason, with no WCAG 2.1 AA violation [TF-172 AC1, ADR-0047]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubAccess(page);
    await page.route(`**/api/v1/users/${record.id}`, (route) =>
      route.fulfill({ json: { ...record, factors: 2, takeover_allowed: true } }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto(`/users/${record.id}`);

    await page.locator("[data-panel=record-header]").getByRole("button", { name: "Reset factors" }).click();
    const dialog = page.getByRole("dialog", { name: `Reset the second factors of ${record.display_name}?` });
    await expect(dialog.getByRole("textbox", { name: "Reason" })).toBeFocused();
    await page.screenshot({ path: `test-results/a11y/user-reset-factors-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`the MFA policy on the settings page in the ${scheme} scheme: the roles chosen by name, with no WCAG 2.1 AA violation [TF-172 AC1, TF-013 AC1, ADR-0047]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubSettings(page);
    const mfaRoles = {
      key: "identity.mfa.roles",
      module: "identity",
      type: "json",
      description: "The codes of the roles whose holders must have a second factor when it is required for roles.",
      requirement: "A list of role codes; an empty list means the roles that hold a high-risk or critical permission.",
      choices: null,
      minimum: null,
      maximum: null,
      whole_number: false,
      scopes: ["tenant"],
      default: [],
      value: ["tenant-administrator"],
      source: {
        level: "tenant",
        unit: null,
        changed_by: { id: "r", name: "Ravi Ravuri" },
        changed_at: "2026-10-22T09:00:00Z",
      },
      set_here: true,
    };
    await page.route("**/api/v1/roles**", (route) => route.fulfill({ json: envelope(route.request().url(), roles) }));
    await page.route("**/api/v1/settings?**", (route) =>
      route.fulfill({ json: { ...tenantSettings, data: [...tenantSettings.data, mfaRoles] } }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/settings");

    const setting = page.getByRole("article", { name: "The roles whose holders need a second factor" });
    await setting.getByRole("button", { name: "Change: The roles whose holders need a second factor" }).click();
    const dialog = page.getByRole("dialog", { name: "Change: The roles whose holders need a second factor" });
    await expect(dialog.getByText("Roles", { exact: true })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/settings-mfa-roles-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`the grace before a second factor is required in the ${scheme} scheme says by when and where, with no WCAG 2.1 AA violation [TF-172 AC1, ADR-0047]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubAccess(page);
    await page.route("**/api/v1/me", (route) =>
      route.fulfill({ json: { ...me, mfa_enrol_by: new Date(Date.now() + 6 * 24 * 60 * 60_000).toISOString() } }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/users");

    const banner = page.locator("[data-mfa-enrol-by]");
    await expect(banner).toContainText("Your organisation requires a second factor. Add one by");
    await expect(banner.getByRole("link", { name: "Add one in My profile." })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/mfa-enrol-by-${scheme}.png` });
    await expectNoViolation(page);
  });
}
