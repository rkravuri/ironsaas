import { defineConfig, devices } from "@playwright/test";

/**
 * The browser path of the smoke (S1 plan T17): login and the Users grid against a running unit.
 * E2E_BASE_URL is the tenant's address (default the smoke tenant on the laptop); E2E_LOGIN_ID and E2E_PASSWORD are
 * the administrator's, which `make e2e` reads from the store where the smoke tool kept them.
 */
export default defineConfig({
  testDir: "./e2e",
  // The accessibility scan has its own config (playwright.a11y.config.ts) and needs no stack.
  testIgnore: ["**/a11y/**"],
  // A folder of its own: Playwright empties its output folder first, and the accessibility scan's screenshots sit beside it.
  outputDir: "test-results/e2e",
  timeout: 60_000,
  expect: { timeout: 15_000 },
  // One browser at a time: the tests share one tenant and one administrator account, and a wrong-password test
  // running beside a sign-in is a real race on the same row (the API survives it since T17, the tests need not).
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  // The JUnit file is make trace's (ADR-0044); outside test-results/e2e, which Playwright empties first, so the last run's
  // results stay until the next run writes them again.
  reporter: [
    ["list"],
    ["junit", { outputFile: "../artifacts/test-results/e2e/junit.xml" }],
    ...(process.env.CI ? [["html", { open: "never" }] as const] : []),
  ],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "https://smoke.localhost",
    ignoreHTTPSErrors: true,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
