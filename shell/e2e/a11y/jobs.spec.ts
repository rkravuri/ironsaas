import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { stubJobs } from "./stubs";

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

for (const scheme of schemes) {
  test(`the jobs page in the ${scheme} scheme: every job with its schedule, class and state in words, a failed one opened with its error, log and runs, the pause by keyboard, and no WCAG 2.1 AA violation [TF-121 AC1, ADR-0061, ADR-0024]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubJobs(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/jobs?job=audit.retention");

    await expect(page.getByRole("heading", { level: 2, name: "Jobs" })).toBeVisible();
    const table = page.getByRole("table", { name: /The jobs of your organisation/ });
    await expect(table.getByRole("row").filter({ hasText: "authz.conflict_report" })).toContainText("Paused");
    await expect(table.getByRole("row").filter({ hasText: "flags.refresh_cache" })).toContainText("In each process");
    await expect(table.getByRole("row").filter({ hasText: "purchasing.close_stale_requests" })).toContainText(
      "Part of the suite procurement",
    );
    const failure = page.locator("[data-job-failure]");
    await expect(failure).toContainText("audit.archive.unavailable: The storage did not answer.");
    await expect(failure).toContainText("Archiving 120 events of class standard.");
    await expect(
      page.getByRole("region", { name: "Runs of audit.retention" }).getByRole("grid").getByText("Dead letter"),
    ).toBeVisible();
    await expect(
      page.getByRole("navigation", { name: "Main navigation" }).getByRole("link", { name: "Jobs" }),
    ).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/jobs-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // Keyboard only: the pause asks for a reason, with the focus on it.
    await page.getByRole("button", { name: "Pause" }).focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Pause audit.retention?" });
    await expect(dialog.getByLabel(/^Reason/)).toBeFocused();
    await page.screenshot({ path: `test-results/a11y/jobs-pause-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`the dead letters page in the ${scheme} scheme: the dead letters of jobs and events in one grid, one opened with its error and its action, and no WCAG 2.1 AA violation [ADR-0061, ADR-0024]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubJobs(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/dead-letters");

    await expect(page.getByRole("heading", { level: 2, name: "Dead letters" })).toBeVisible();
    const grid = page.getByRole("region", { name: "Dead letters of jobs and events" }).getByRole("grid");
    await expect(grid.getByText("Job run")).toBeVisible();
    await expect(grid.getByText("Event", { exact: true })).toBeVisible();
    await grid.getByText("audit.retention").click();
    const detail = page.getByRole("region", { name: "audit.retention" });
    await expect(detail).toContainText("failed 5 times");
    await expect(detail.getByRole("button", { name: "Run again" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/dead-letters-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });
}
