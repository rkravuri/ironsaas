import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { me, passwordPolicy, record, stubAccess, stubPasswords } from "./stubs";

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

/** Nobody signed in: /me answers 401, the policy is the tenant's. */
async function anonymous(page: Page): Promise<void> {
  await page.route("**/api/**", (route) =>
    route.fulfill({
      status: 401,
      contentType: "application/problem+json",
      body: JSON.stringify({ status: 401, error_code: "identity.unauthenticated" }),
    }),
  );
  await page.route("**/auth/password-policy", (route) => route.fulfill({ json: passwordPolicy }));
}

const refuse = (code: string, field: string, message: string) => ({
  status: 400,
  contentType: "application/problem+json",
  body: JSON.stringify({
    status: 400,
    error_code: `identity.password.${code}`,
    detail: message,
    correlation_id: "corr-policy",
    errors: [{ field, code, message }],
  }),
});

for (const scheme of schemes) {
  test(`signing in in the ${scheme} scheme: Forgot your password, and the locked message, with no WCAG 2.1 AA violation [TF-172 AC1, TF-012 AC2, ADR-0046]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await anonymous(page);
    await page.route("**/auth/login", (route) =>
      route.fulfill({
        status: 401,
        contentType: "application/problem+json",
        body: JSON.stringify({ status: 401, error_code: "identity.login.locked", correlation_id: "corr-locked" }),
      }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/login");

    await expect(page.getByText("Forgot your password?")).toBeVisible();
    await page.getByRole("textbox", { name: /Login identifier/ }).fill("ada@acme.localhost");
    await page.getByLabel(/^Password/).fill("not the password");
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByRole("alert")).toContainText("Too many failed sign-ins for this login.");
    await page.screenshot({ path: `test-results/a11y/login-locked-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`a recovery link's page in the ${scheme} scheme: the policy in words and the rule a refusal names, with no WCAG 2.1 AA violation [TF-172 AC1, TF-012 AC1, TF-017 AC1, ADR-0046]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await anonymous(page);
    await page.route("**/auth/recovery/a-code", (route) =>
      route.fulfill(refuse("common", "password", "Choose a password that is not common.")),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/recovery/a-code");

    const rules = page.getByRole("list", { name: "Your password needs" });
    await expect(rules).toContainText("Not one of your last 10 passwords.");
    await page.screenshot({ path: `test-results/a11y/recovery-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByLabel(/^New password/).fill("qwertyuiop123");
    await page.getByLabel(/^Repeat the new password/).fill("qwertyuiop123");
    await page.getByRole("button", { name: "Save the new password" }).click();
    await expect(rules.locator("[data-failed=true]")).toContainText("Your password did not meet this rule.");
    await page.screenshot({ path: `test-results/a11y/recovery-refused-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`an invitation's page in the ${scheme} scheme shows the policy in words, with no WCAG 2.1 AA violation [TF-172 AC1, TF-018 AC1, ADR-0046]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await anonymous(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/invitations/a-token");

    await expect(page.getByRole("list", { name: "Your password needs" })).toContainText("At least 12 characters.");
    await page.screenshot({ path: `test-results/a11y/invitation-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`the user record's actions in the ${scheme} scheme: unlock, a recovery link shown once, deactivate, with no WCAG 2.1 AA violation [TF-172 AC1, TF-017 AC1, ADR-0046]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubAccess(page);
    const locked = { ...record, locked_until: new Date(Date.now() + 10 * 60_000).toISOString() };
    await page.route(`**/api/v1/users/${record.id}`, (route) => route.fulfill({ json: locked }));
    await page.route(`**/api/v1/users/${record.id}/recovery-link`, (route) =>
      route.fulfill({ json: { recovery_link: "/recovery/a-code-shown-once", expires_at: "2026-10-21T10:15:00Z" } }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto(`/users/${record.id}`);

    const header = page.locator("[data-panel=record-header]");
    await expect(header.getByRole("button", { name: "Unlock" })).toBeVisible();
    await expect(page.getByText("Login locked until")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/user-actions-${scheme}.png` });
    await expectNoViolation(page);

    await header.getByRole("button", { name: "Unlock" }).click();
    const unlock = page.getByRole("dialog", { name: `Unlock the login of ${record.display_name}?` });
    await expect(unlock).toContainText("too many failed sign-ins");
    await page.screenshot({ path: `test-results/a11y/user-unlock-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");
    await expect(unlock).toBeHidden();

    await header.getByRole("button", { name: "Deactivate" }).click();
    const deactivate = page.getByRole("dialog", { name: `Deactivate ${record.display_name}?` });
    await expect(deactivate).toContainText("every session ends now");
    await page.screenshot({ path: `test-results/a11y/user-deactivate-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");
    await expect(deactivate).toBeHidden();

    await header.getByRole("button", { name: "Issue a recovery link" }).click();
    const confirm = page.getByRole("dialog", { name: `Issue a recovery link for ${record.display_name}?` });
    await expect(confirm).toContainText("once, within 15 minutes");
    await page.screenshot({ path: `test-results/a11y/user-recovery-${scheme}.png` });
    await expectNoViolation(page);
    await confirm.getByRole("button", { name: "Issue the link" }).click();
    const shown = page.getByRole("dialog", { name: `A recovery link for ${record.display_name}` });
    await expect(shown.getByRole("textbox", { name: "Recovery link" })).toHaveValue(/\/recovery\/a-code-shown-once$/);
    await expect(shown.getByRole("textbox", { name: "Recovery link" })).toBeFocused();
    await page.screenshot({ path: `test-results/a11y/user-recovery-link-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`a deactivated person's record in the ${scheme} scheme offers Reactivate, with no WCAG 2.1 AA violation [TF-172 AC1, ADR-0046]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubAccess(page);
    await page.route(`**/api/v1/users/${record.id}`, (route) =>
      route.fulfill({ json: { ...record, status: "disabled" } }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto(`/users/${record.id}`);

    const header = page.locator("[data-panel=record-header]");
    await expect(header).toContainText("Deactivated");
    await header.getByRole("button", { name: "Reactivate" }).click();
    const reactivate = page.getByRole("dialog", { name: `Reactivate ${record.display_name}?` });
    await expect(reactivate).toContainText("can sign in again");
    await page.screenshot({ path: `test-results/a11y/user-reactivate-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`the password expiry warning in the ${scheme} scheme says when and where to change it, with no WCAG 2.1 AA violation [TF-172 AC1, TF-012 AC5, ADR-0046]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubAccess(page);
    await stubPasswords(page);
    await page.route("**/api/v1/me", (route) =>
      route.fulfill({
        json: {
          ...me,
          permissions: ["identity.users.read"],
          permissions_anywhere: ["identity.users.read"],
          password_expires_at: new Date(Date.now() + 5 * 24 * 60 * 60_000).toISOString(),
        },
      }),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/users");

    const warning = page.locator("[data-password-expiry]");
    await expect(warning).toContainText("Your password expires in 5 days");
    await expect(warning.getByRole("link", { name: "Change it in My profile." })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/password-expiry-${scheme}.png` });
    await expectNoViolation(page);
  });
}
