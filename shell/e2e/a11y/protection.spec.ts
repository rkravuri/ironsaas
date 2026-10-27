import { createHash } from "node:crypto";
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { stubConsole } from "./stubs";

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

const salt = "5f1d2c3b4a596877?expires=4102444800";
const challenge = {
  algorithm: "SHA-256",
  challenge: createHash("sha256").update(`${salt}23`).digest("hex"),
  max_number: 40,
  salt,
  signature: "a".repeat(64),
};

const problem = (status: number, body: Record<string, unknown>, headers: Record<string, string> = {}) => ({
  status,
  contentType: "application/problem+json",
  headers,
  body: JSON.stringify({ status, correlation_id: "corr-protection", ...body }),
});

/** Nobody signed in; the login answers what each scan sets. */
async function anonymous(page: Page): Promise<void> {
  await page.route("**/api/**", (route) => route.fulfill(problem(401, { error_code: "identity.unauthenticated" })));
  await page.route("**/auth/sign-in-options", (route) => route.fulfill({ json: { passkey_sign_in: false } }));
  await page.route("**/auth/challenge", (route) => route.fulfill({ json: challenge }));
}

const setting = (key: string, over: Record<string, unknown>) => ({
  key,
  module: "protection",
  type: "number",
  description: "",
  requirement: "",
  choices: null,
  minimum: null,
  maximum: null,
  whole_number: true,
  scopes: ["tenant"],
  default: null,
  value: null,
  source: { level: "default", unit: null, changed_by: null, changed_at: null },
  set_here: false,
  ...over,
});

const protection = {
  scope: "tenant",
  unit: null,
  data: [
    setting("protection.cors.allowed_origins", {
      type: "json",
      whole_number: false,
      description: "The web origins whose pages may call this tenant's API from a browser.",
      requirement:
        "A list of at most 20 origins, each a scheme and a host with an optional port, like https://portal.example.com, without a path.",
      default: [],
      value: ["https://portal.example.com"],
      source: {
        level: "tenant",
        unit: null,
        changed_by: { id: "01a15100-0000-7000-8000-0000000000d1", name: "Ravi Ravuri" },
        changed_at: "2026-10-27T10:00:00Z",
      },
      set_here: true,
    }),
    setting("protection.limits.login_per_minute", {
      description: "How many sign-in attempts one address may make in a minute.",
      requirement: "A whole number within the bounds your platform operator set, by default from 3 to 60 a minute.",
      default: 10,
      value: 10,
    }),
    setting("protection.limits.api_per_minute", {
      description:
        "How many calls one person or client, or one address before sign-in, may make to the API in a minute.",
      requirement: "A whole number within the bounds your platform operator set, by default from 60 to 6000 a minute.",
      default: 600,
      value: 600,
    }),
  ],
};

for (const scheme of schemes) {
  test(`the login form in the ${scheme} scheme: the check after repeated failures, solved, and a rate limit's wait in words, with no WCAG 2.1 AA violation [TF-172 AC1, TF-072 AC1, TF-072 AC4, ADR-0053]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 860 });
    await anonymous(page);
    let attempts = 0;
    await page.route("**/auth/login", (route) =>
      route.fulfill(
        attempts++ === 0
          ? problem(403, { error_code: "identity.login.challenge_required" })
          : problem(
              429,
              { error_code: "protection.rate_limited", retry_after_seconds: 42, limit: "login" },
              { "Retry-After": "42" },
            ),
      ),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/login");
    await page.getByRole("textbox", { name: /Login identifier/ }).fill("ada@acme.localhost");
    await page.getByLabel(/^Password/).fill("not the password");
    await page.getByRole("button", { name: "Sign in" }).click();

    const check = page.getByRole("checkbox", { name: "I am a person, not a program" });
    await expect(check).toBeVisible();
    await expect(page.getByRole("alert")).toContainText("Tick the box below, then sign in.");
    await page.screenshot({ path: `test-results/a11y/login-check-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // Keyboard only: the box ticks with Space, and the form says in words when the check is done.
    await check.focus();
    await page.keyboard.press("Space");
    await expect(page.getByText("Checked. Select Sign in.")).toBeVisible();
    await expect(check).toBeChecked();
    await page.screenshot({ path: `test-results/a11y/login-checked-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByRole("alert")).toContainText(
      "Too many requests in a short time. Wait 42 seconds, then try again.",
    );
    await page.screenshot({ path: `test-results/a11y/login-limited-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`the protection settings in the ${scheme} scheme: the other sites allowed, edited one origin at a time, and the rate limits with their units, with no WCAG 2.1 AA violation [TF-172 AC1, TF-070 AC2, TF-072 AC3, ADR-0053]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubConsole(page);
    await page.route("**/api/v1/settings?**", (route) => route.fulfill({ json: protection }));
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/settings");

    await expect(page.getByRole("heading", { level: 3, name: "Protection of the sign-in and the API" })).toBeVisible();
    const origins = page.getByRole("article", { name: "Other sites allowed to call the API from a browser" });
    await expect(origins).toContainText("https://portal.example.com");
    await expect(page.getByRole("article", { name: "Sign-in attempts from one address" })).toContainText(
      "10 attempts a minute",
    );
    await page.screenshot({ path: `test-results/a11y/settings-protection-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    const change = origins.getByRole("button", { name: "Change: Other sites allowed to call the API from a browser" });
    await change.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Change: Other sites allowed to call the API from a browser" });
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText("Type an origin, such as https://portal.example.com, and press Enter.");
    await page.screenshot({ path: `test-results/a11y/settings-origins-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
  });
}
