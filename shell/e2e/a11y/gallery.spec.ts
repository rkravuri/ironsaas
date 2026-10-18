import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

type Violation = Awaited<ReturnType<AxeBuilder["analyze"]>>["violations"][number];

const schemes = ["light", "dark"] as const;
const densities = ["comfortable", "compact"] as const;
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

for (const scheme of schemes) {
  for (const density of densities) {
    test(`the gallery in the ${scheme} scheme at ${density} density has no WCAG 2.1 AA violation`, async ({ page }) => {
      // Reduced motion makes every transition a cut, so axe measures settled states; the scheme emulation keeps
      // Mantine's "auto" in step with the gallery's own switch.
      // No API behind the preview: every call answers "not signed in", which is the state the public gallery expects.
      await page.route("**/api/**", (route) => route.fulfill({ status: 401, contentType: "application/problem+json", body: JSON.stringify({ status: 401, error_code: "identity.unauthenticated" }) }));
      await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
      await page.goto(`/gallery?scheme=${scheme}&density=${density}`);
      await expect(page.locator("[data-gallery-ready='true']")).toBeAttached();
      // The four pictures the review looks at, next to the report.
      await page.screenshot({ path: `test-results/gallery-${scheme}-${density}.png`, fullPage: true });

      const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
      const violations = results.violations.filter(
        (violation) => !knownIssues.some((known) => known.rule === violation.id && targetsOf(violation).every((target) => target.startsWith(known.target))),
      );
      expect(violations.map(describeViolation)).toEqual([]);
    });
  }
}
