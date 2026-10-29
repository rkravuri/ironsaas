import { expect, test, type Page } from "@playwright/test";
import { call, open, signIn } from "./helpers";

interface Rule {
  name: string;
  effective: { threshold: number; window_minutes: number } | null;
  state: string;
}

interface Alert {
  id: string;
  rule: string;
  state: string;
  count: number;
  group_value: string;
}

/** The rules run every minute in the unit's worker; two passes and the window's settling are within this. */
const alertWithin = 150_000;

async function alertFor(page: Page, login: string): Promise<Alert> {
  let found: Alert | undefined;
  await expect
    .poll(
      async () => {
        const answer = await call<{ data: Alert[] }>(
          page,
          "GET",
          `/api/v1/alerts?filter=${encodeURIComponent("rule:eq:sign_in_failures_by_login")}&filter=${encodeURIComponent(`group_value:eq:${login}`)}`,
        );
        found = answer.body.data[0];
        return found?.id ?? "";
      },
      { timeout: alertWithin, intervals: [3_000] },
    )
    .not.toBe("");
  return found!;
}

/**
 * TF-079 and ADR-0056 on a real unit: the tenant makes the rule for failed sign-ins of one login identifier stricter (2
 * in 10 minutes rather than 10), two failed sign-ins of an identifier nobody has raise its alert within the rules'
 * minutes, and the console's Security alerts page acknowledges and resolves it, with the link to its events; the rule
 * returns to the operator's values at the end, whatever the result. Two failures of an unknown identifier lock nothing.
 */
test.describe("the tenant's security alerts", () => {
  test("a stricter rule turns two failed sign-ins into an alert, which the page acknowledges and resolves with a note [TF-079 AC1, ADR-0056]", async ({
    page,
  }) => {
    test.setTimeout(240_000);
    await page.emulateMedia({ reducedMotion: "reduce" });
    await signIn(page);
    const login = `e2e-alert-${Date.now().toString(36)}@nobody.invalid`;
    try {
      const stricter = await call<Rule>(page, "PUT", "/api/v1/alert-rules/sign_in_failures_by_login", {
        threshold: 2,
        window_minutes: null,
        above: null,
      });
      expect(stricter.status).toBe(200);
      expect(stricter.body.effective?.threshold).toBe(2);
      const looser = await call<Rule>(page, "PUT", "/api/v1/alert-rules/sign_in_failures_by_login", {
        threshold: 50,
        window_minutes: null,
        above: null,
      });
      expect(looser.status, "a tenant cannot loosen a rule").toBe(400);

      for (let attempt = 0; attempt < 2; attempt++) {
        const failed = await call(page, "POST", "/auth/login", { login_id: login, password: "not the password" });
        expect(failed.status).toBe(401);
      }

      const alert = await alertFor(page, login);
      expect(alert.count).toBeGreaterThanOrEqual(2);

      await open(page, `/console/security-alerts?alert=${alert.id}`);
      const detail = page.getByRole("region", { name: "Many failed sign-ins for one login identifier" });
      await expect(detail).toContainText(login);
      await expect(detail.getByRole("link", { name: "Open the events in the audit trail" })).toBeVisible();
      await detail.getByRole("button", { name: "Acknowledge" }).click();
      await page
        .getByRole("dialog", { name: "Acknowledge the alert?" })
        .getByRole("button", { name: "Acknowledge" })
        .click();
      await expect(page.getByText(/You acknowledged/)).toBeVisible();
      await detail.getByRole("button", { name: "Resolve" }).click();
      const dialog = page.getByRole("dialog", { name: "Resolve the alert?" });
      await dialog.getByLabel(/^Note/).fill("The end-to-end tests' own failed sign-ins.");
      await dialog.getByRole("button", { name: "Resolve" }).click();
      await expect(page.getByText(/You resolved/)).toBeVisible();
      await page.screenshot({ path: "test-results/e2e/security-alerts.png", fullPage: true });

      const resolved = await call<Alert>(page, "GET", `/api/v1/alerts/${alert.id}`);
      expect(resolved.body.state).toBe("resolved");

      // The link leads to the audit search with the alert's events.
      await detail.getByRole("link", { name: "Open the events in the audit trail" }).click();
      await expect(page).toHaveURL(/\/audit\?event_type=identity\.login_failed/);
      await expect(page.getByText(/The events behind a security alert/)).toBeVisible();
      await expect(page.getByRole("grid").getByText("Sign-in failed").first()).toBeVisible();
    } finally {
      const back = await call(page, "PUT", "/api/v1/alert-rules/sign_in_failures_by_login", {
        threshold: null,
        window_minutes: null,
        above: null,
      });
      expect(back.status, "the rule returns to the operator's values").toBe(200);
    }
  });

  test("the Alert rules page shows every rule running with its values [TF-079 AC4, ADR-0056]", async ({ page }) => {
    await signIn(page);
    const rules = await call<{ data: Rule[] }>(page, "GET", "/api/v1/alert-rules");
    expect(rules.status).toBe(200);
    expect(rules.body.data.filter((rule) => rule.state !== "running").map((rule) => rule.name)).toEqual([]);

    await open(page, "/console/alert-rules");
    const table = page.getByRole("table", { name: /Each rule/ });
    await expect(table.getByRole("row", { name: /Many permission denials/ })).toContainText(
      "100 events within 10 minutes",
    );
    await expect(table.getByRole("row", { name: /Someone signed in as another person/ })).toContainText(
      "Each event, at once",
    );
    await page.screenshot({ path: "test-results/e2e/alert-rules.png", fullPage: true });
  });
});
