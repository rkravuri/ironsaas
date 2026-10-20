import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { reviewId, stubDelegations, stubReviews } from "./stubs-delegation";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

async function expectNoViolation(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
  expect(results.violations.map((violation) => `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => `${node.target.map(String).join(" ")} (${node.failureSummary ?? ""})`).join("; ")}`)).toEqual([]);
}

for (const scheme of schemes) {
  test(`a person's own delegations in My profile in the ${scheme} scheme: in words with their status, the create dialog by keyboard, and no WCAG 2.1 AA violation`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubDelegations(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/me");

    await page.getByRole("tab", { name: "Delegations" }).click();
    const yours = page.getByRole("list", { name: "Your delegations" });
    await expect(yours).toContainText("Ada Lovelace hands you user administration");
    await expect(yours).toContainText("You hand user administration to Grace Hopper");
    await expect(yours.getByText("Scheduled")).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/my-delegations-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // Keyboard only: the dialog opens on Enter, takes focus, offers duties in words, and closes with Escape back to its button.
    const open = page.getByRole("button", { name: "Delegate your duties" });
    await open.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Delegate your duties" });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("checkbox", { name: "user administration" })).toBeVisible();
    await dialog.getByRole("combobox", { name: "To whom" }).fill("gr");
    await expect(page.getByRole("option", { name: "Grace Hopper, North" })).toBeVisible();
    await page.getByRole("option", { name: "Grace Hopper, North" }).click();
    await dialog.getByRole("checkbox", { name: "user administration" }).check();
    await dialog.getByRole("textbox", { name: "Reason" }).fill("Annual leave.");
    await page.screenshot({ path: `test-results/a11y/my-delegations-create-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(open).toBeFocused();
  });

  test(`the console's delegations page for administrators in the ${scheme} scheme: others' delegations in the grid, delegating for someone, and no WCAG 2.1 AA violation`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubDelegations(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/delegations");

    await expect(page.getByRole("heading", { level: 2, name: "Delegations" })).toBeVisible();
    await expect(page.getByRole("gridcell", { name: "Linus Pauling" }).first()).toBeVisible();
    await expect(page.getByRole("button", { name: "End the chosen delegation" })).toBeDisabled();
    await page.screenshot({ path: `test-results/a11y/delegations-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "Delegate for someone" }).click();
    const dialog = page.getByRole("dialog", { name: "Delegate someone's duties" });
    await expect(dialog.getByRole("combobox", { name: "Whose duties" })).toBeVisible();
    await expect(dialog.getByRole("combobox", { name: "To whom" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/delegations-create-${scheme}.png` });
    await expectNoViolation(page);
    // The focus starts in the person picker, which keeps Escape for its own list; Cancel closes the dialog.
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(dialog).toBeHidden();
  });

  test(`the Acting for control and its band in the ${scheme} scheme: the choice in the header, whose rights until when, how to stop, and no WCAG 2.1 AA violation`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubDelegations(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/");

    const control = page.getByRole("button", { name: "Act for someone who delegated duties to you" });
    await expect(control).toBeVisible();
    await control.click();
    const menu = page.getByRole("menu");
    await expect(menu.getByRole("menuitem", { name: /Myself/ })).toBeVisible();
    await expect(menu.getByRole("menuitem", { name: /Ada Lovelace/ })).toContainText("user administration, until");
    await page.screenshot({ path: `test-results/a11y/acting-control-${scheme}.png` });
    await expectNoViolation(page);

    await menu.getByRole("menuitem", { name: /Ada Lovelace/ }).click();
    const band = page.getByRole("status").filter({ hasText: "You are acting for Ada Lovelace" });
    await expect(band).toBeVisible();
    await expect(band).toContainText("You use Ada Lovelace's rights for user administration until");
    await expect(page.getByRole("button", { name: /Acting for Ada Lovelace/ })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/acting-band-${scheme}.png` });
    await expectNoViolation(page);

    await band.getByRole("button", { name: "Act as myself" }).click();
    await expect(band).toBeHidden();
  });

  test(`the access review pages in the ${scheme} scheme: the campaigns with their progress, a campaign's items to decide, the revoke dialog, and no WCAG 2.1 AA violation`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubReviews(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/console/access-reviews");

    await expect(page.getByRole("heading", { level: 2, name: "Access reviews" })).toBeVisible();
    await expect(page.getByRole("gridcell", { name: "Finance year end" })).toBeVisible();
    await expect(page.getByRole("gridcell", { name: "10 of 14 decided. 4 are overdue." })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/reviews-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.goto(`/console/access-reviews/${reviewId}`);
    await expect(page.getByRole("heading", { name: "Quarterly review, Operations" })).toBeVisible();
    await expect(page.getByRole("gridcell", { name: "Marie Curie" })).toBeVisible();
    // The viewer's own row says who decides it, and the page's confirmation leaves it out.
    await expect(page.getByRole("gridcell", { name: "About your own access: another reviewer decides it." })).toBeVisible();
    await expect(page.getByRole("gridcell", { name: "No other reviewer here: the review's owner decides it." })).toBeVisible();
    await expect(page.getByRole("button", { name: "Confirm the 4 items on this page" })).toBeEnabled();
    await page.screenshot({ path: `test-results/a11y/review-record-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("gridcell", { name: "Marie Curie" }).click();
    await page.getByRole("button", { name: "Revoke" }).click();
    const dialog = page.getByRole("dialog", { name: "Revoke this role" });
    await expect(dialog).toContainText("Marie Curie loses Store manager at");
    await page.screenshot({ path: `test-results/a11y/review-revoke-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
  });
}
