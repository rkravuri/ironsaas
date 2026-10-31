import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { domainEvent, stubEvents } from "./stubs";

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
  test(`the domain events page in the ${scheme} scheme: the events with their status in words, one opened with its data and subscribers, the dead letters, the replay by keyboard, and no WCAG 2.1 AA violation [TF-112 AC1, ADR-0060, ADR-0024]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubEvents(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto(`/console/events?event=${domainEvent.id}`);

    await expect(page.getByRole("heading", { level: 2, name: "Domain events" })).toBeVisible();
    const grid = page.getByRole("region", { name: "Domain events" }).getByRole("grid");
    await expect(grid.getByText("authz.delegation.ended (version 1)")).toBeVisible();
    await expect(grid.getByText("Dead letter")).toBeVisible();
    const detail = page.getByRole("region", { name: "identity.person.deactivated (version 1)" });
    await expect(detail).toContainText('"sessions_ended": 2');
    await expect(detail).toContainText("authz.delegation.locked: The delegation is being changed.");
    await expect(page.getByText(/Events up to .* are archived/)).toBeVisible();
    await expect(
      page.getByRole("navigation", { name: "Main navigation" }).getByRole("link", { name: "Domain events" }),
    ).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/events-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // Keyboard only: the replay asks for a reason, with the focus on it.
    await detail.getByRole("button", { name: "Replay to authz.end_delegations_of_deactivated" }).focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Replay the event?" });
    await expect(dialog.getByLabel(/^Reason/)).toBeFocused();
    await page.screenshot({ path: `test-results/a11y/events-replay-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");

    await page.getByRole("tab", { name: "Dead letters" }).click();
    const dead = page.getByRole("region", { name: "Dead letters" }).getByRole("grid");
    await expect(dead.getByText("authz.end_delegations_of_deactivated")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/events-dead-letters-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });
}
