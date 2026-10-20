import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

type Violation = Awaited<ReturnType<AxeBuilder["analyze"]>>["violations"][number];

const schemes = ["light", "dark"] as const;
const densities = ["comfortable", "compact"] as const;
const tenants = ["acme", "bravo"] as const;
// WCAG 2.0 and 2.1, levels A and AA: the done criterion, nothing more (best-practice rules are not in these tags).
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

/**
 * A finding that cannot be fixed in this repository (an AG Grid Community internal, for example). Empty on purpose:
 * an entry names the rule id, the selector prefix it is limited to, and the upstream issue in a comment beside it.
 */
const knownIssues: { rule: string; target: string }[] = [];

const targetsOf = (violation: Violation): string[] => violation.nodes.map((node) => node.target.map(String).join(" "));

const describeViolation = (violation: Violation): string =>
  `${violation.id} (${violation.impact ?? "unknown"}): ${violation.help} at ${targetsOf(violation).join("; ")} (${violation.helpUrl})`;

async function prepare(page: Page, scheme: (typeof schemes)[number]): Promise<void> {
  // No API behind the preview: every call answers "not signed in", which is the state the public pages expect.
  await page.route("**/api/**", (route) =>
    route.fulfill({
      status: 401,
      contentType: "application/problem+json",
      body: JSON.stringify({ status: 401, error_code: "identity.unauthenticated" }),
    }),
  );
  // Reduced motion makes every transition a cut, so axe measures settled states; the scheme emulation keeps
  // Mantine's "auto" in step with the gallery's own switch.
  await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
}

async function scan(page: Page): Promise<string[]> {
  const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
  return results.violations
    .filter(
      (violation) =>
        !knownIssues.some(
          (known) =>
            known.rule === violation.id && targetsOf(violation).every((target) => target.startsWith(known.target)),
        ),
    )
    .map(describeViolation);
}

for (const scheme of schemes) {
  for (const density of densities) {
    test(`the gallery in the ${scheme} scheme at ${density} density has no WCAG 2.1 AA violation`, async ({ page }) => {
      await prepare(page, scheme);
      await page.goto(`/gallery?scheme=${scheme}&density=${density}`);
      await expect(page.locator("[data-gallery-ready='true']")).toBeAttached();
      // The pictures the review looks at, next to the report.
      await page.screenshot({ path: `test-results/a11y/gallery-${scheme}-${density}.png`, fullPage: true });
      if (density === "comfortable") {
        await page
          .locator("section[aria-labelledby='gallery-drawings']")
          .screenshot({ path: `test-results/a11y/gallery-drawings-${scheme}.png` });
        await page.locator("#gallery-record-page").screenshot({ path: `test-results/a11y/record-page-${scheme}.png` });
      }
      expect(await scan(page)).toEqual([]);
    });
  }

  for (const tenant of tenants) {
    test(`the login page of ${tenant} in the ${scheme} scheme keeps the form above the fold and has no WCAG 2.1 AA violation`, async ({
      page,
    }) => {
      // The smallest desktop the brief names: the form must be in view without scrolling.
      await page.setViewportSize({ width: 1024, height: 768 });
      await prepare(page, scheme);
      // The hostname is the tenant: a .localhost name resolves to the preview, and the mark is seeded from it.
      await page.goto(`http://${tenant}.localhost:4180/login`);
      await expect(page.getByRole("button", { name: /sign in/i })).toBeInViewport();
      await expect(page.locator(`svg[data-mandala='${tenant}.localhost']`).first()).toBeVisible();
      await page.screenshot({ path: `test-results/a11y/login-${tenant}-${scheme}.png` });
      expect(await scan(page)).toEqual([]);
    });
  }
}
