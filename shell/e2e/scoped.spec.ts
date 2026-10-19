import { expect, test, type Locator } from "@playwright/test";
import { loginId, open, signIn } from "./helpers";

/**
 * A child-unit administrator against a real unit (A2, ADR-0031). `make e2e` sets up the data through the API first
 * (infra/compose/scripts/e2e-data.py): a child unit, this user in it with a role that reads users at the unit and its
 * children, and a colleague beside them.
 */
const child = { loginId: process.env.E2E_CHILD_LOGIN_ID ?? "", password: process.env.E2E_CHILD_PASSWORD ?? "" };
const outsideUserId = process.env.E2E_OUTSIDE_USER_ID ?? "";

const loginCells = (grid: Locator) => grid.locator('[role="gridcell"][col-id="login_id"]');

test.describe("a child-unit administrator", () => {
  test.skip(child.loginId === "", "make e2e sets up the child unit's data first");

  for (const scheme of ["light", "dark"] as const) {
    test(`sees only that unit's users in the ${scheme} scheme, and a user outside is not found by URL`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: scheme });
      await signIn(page, child);

      // The navigation follows the permissions held anywhere: Users, and nothing of the console.
      await expect(page.getByRole("link", { name: "Users" })).toBeVisible();
      await expect(page.getByRole("link", { name: "Roles" })).toHaveCount(0);
      await expect(page.getByRole("link", { name: "Organisation units" })).toHaveCount(0);

      await open(page, "/users");
      const grid = page.getByRole("region", { name: "Users" });
      await expect(loginCells(grid).filter({ hasText: child.loginId })).toBeVisible();
      await expect(loginCells(grid).filter({ hasText: loginId })).toHaveCount(0);
      // Everyone listed lives in the child unit: the administrator made there, and the colleague beside them.
      for (const cell of await loginCells(grid).all()) {
        await expect(cell).toHaveText(/^(child-admin|colleague)-[0-9a-f]+@/);
      }
      await page.screenshot({ path: `test-results/e2e/users-child-${scheme}.png` });

      // The tenant administrator lives at the root, outside: not found, in the page and in the API.
      await open(page, `/users/${outsideUserId}`);
      await expect(page.getByRole("heading", { name: "This user is not here" })).toBeVisible();
      // Asked from the page, with its cookie: Node's resolver behind page.request does not know *.localhost, Chromium does.
      const answer = await page.evaluate(async (id) => {
        const response = await fetch(`/api/v1/users/${id}`, { headers: { Accept: "application/json" } });
        return { status: response.status, body: (await response.json()) as { error_code?: string } };
      }, outsideUserId);
      expect(answer.status).toBe(404);
      expect(answer.body.error_code).toBe("identity.user.not_found");
      await page.screenshot({ path: `test-results/e2e/user-outside-child-${scheme}.png` });
    });
  }
});
