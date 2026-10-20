import { defineConfig, devices } from "@playwright/test";

/**
 * The accessibility scan (ADR-0024 item 7, ADR-0025): axe over the gallery in both schemes and both densities, against
 * `vite preview` of the gallery build. It needs no stack, so it has its own config; `make e2e` keeps the smoke tenant.
 */
export default defineConfig({
  testDir: "./e2e/a11y",
  // A folder of its own, beside the browser tests' test-results/e2e: Playwright empties its output folder before a run,
  // and the default, test-results itself, took the browser tests' screenshots with it. The specs write theirs here too.
  outputDir: "test-results/a11y",
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  // No retries: the page has no network and no motion during the scan, so a second run would only hide a real finding.
  retries: 0,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : [["list"]],
  use: {
    baseURL: "http://localhost:4180",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  // A port of its own: the production preview on 4173 has no gallery, and a reused server must be a gallery one.
  webServer: {
    command: "npm run gallery:preview",
    url: "http://localhost:4180/",
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
  },
});
