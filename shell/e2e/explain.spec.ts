import { expect, test } from "@playwright/test";
import { call, open, signIn } from "./helpers";

/**
 * Explaining access and conflict rules on a real unit (A9, ADR-0039), as the tenant's administrator. `make e2e` sets
 * up the data first (infra/compose/scripts/e2e-data.py): the child-unit administrator, who reads users at their unit
 * and below and holds nothing for the whole organisation, and a colleague beside them.
 */
const childId = process.env.E2E_CHILD_ID ?? "";
const childUnitId = process.env.E2E_CHILD_UNIT_ID ?? "";
const colleagueId = process.env.E2E_COLLEAGUE_ID ?? "";

interface Created {
  id: string;
}

test.describe("explaining access and conflict rules", () => {
  test.skip(childId === "" || colleagueId === "", "make e2e sets up the child unit's data first");

  for (const scheme of ["light", "dark"] as const) {
    test(`names the first failing check of a child-unit person's role import in the ${scheme} scheme`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: scheme });
      await signIn(page);

      // From the person's record: "Why can't they...?" opens the explanation with the person chosen.
      await open(page, `/users/${childId}`);
      await page.getByRole("tab", { name: "Access" }).click();
      await page.getByRole("button", { name: "Why can't they…?" }).click();
      await expect(page.getByRole("heading", { level: 2, name: "Explain access" })).toBeVisible();
      await expect(page.getByRole("combobox", { name: "Person" })).toHaveValue(/^Child administrator/);

      const action = page.getByRole("combobox", { name: "Action" });
      await action.fill("Compare a role export");
      await page.getByRole("option", { name: /^Compare a role export with this tenant's roles/ }).click();
      await page.getByRole("button", { name: "Explain" }).click();

      // Echo's plan has role transfer and the import kill switch is off, so the permission is what refuses: the
      // child holds nothing for the whole organisation.
      await expect(page.getByRole("heading", { name: /^Child administrator cannot: Compare a role export .* The system answers 403\.$/ })).toBeVisible();
      await expect(page.getByText("The first check that refuses it: The permission.")).toBeVisible();
      const checks = page.getByRole("list", { name: "The checks, in order" }).getByRole("listitem");
      await expect(checks).toHaveCount(5);
      await expect(checks.nth(0)).toContainText("The person: passed");
      await expect(checks.nth(1)).toContainText("The subscription: passed");
      await expect(checks.nth(2)).toContainText("Flags and kill switches: passed");
      await expect(checks.nth(3)).toContainText("The permission: refused");
      await expect(checks.nth(3)).toContainText('Child administrator does not hold the permission "Create, change and delete tenant roles" for the whole organisation.');
      await expect(checks.nth(4)).toContainText("The record's policy: not reached");
      await page.screenshot({ path: `test-results/e2e/explain-child-${scheme}.png`, fullPage: true });

      // The API answers the same.
      const answer = await call<{ allowed: boolean; status: number; checks: { check: string; result: string }[] }>(page, "GET", `/api/v1/access/explain?principal=${childId}&action=ImportRoles`);
      expect(answer.status).toBe(200);
      expect(answer.body.allowed).toBe(false);
      expect(answer.body.status).toBe(403);
      expect(answer.body.checks.find((check) => check.result === "failed")?.check).toBe("permission");
    });

    test(`refuses a grant that a blocking conflict rule forbids, with the rule named, in the ${scheme} scheme`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: scheme });
      await signIn(page);
      const stamp = `${Date.now().toString(36)}${scheme[0]}`;
      const made: { roles: string[]; rule?: string; assignments: string[] } = { roles: [], assignments: [] };
      try {
        // Two roles of this test's own, so the rule meets nothing another test grants or delegates.
        for (const side of ["first", "second"]) {
          const role = await call<Created>(page, "POST", "/api/v1/roles", {
            code: `e2e-sod-${side}-${stamp}`,
            name: `E2E ${side} side ${stamp}`,
            description: "Made by the conflict rules browser test; removed at its end.",
            principal_type: "user",
            permissions: ["identity.users.read"],
          });
          expect(role.status).toBe(201);
          made.roles.push(role.body.id);
        }
        const [first, second] = made.roles as [string, string];
        const held = await call<Created>(page, "POST", "/api/v1/role-assignments", { principal_id: colleagueId, role_id: first, scope_kind: "unit", unit_id: childUnitId });
        expect(held.status).toBe(201);
        made.assignments.push(held.body.id);
        const ruleName = `E2E keep apart ${stamp}`;
        const rule = await call<Created>(page, "POST", "/api/v1/conflict-rules", {
          name: ruleName,
          first: { kind: "role", value: first },
          second: { kind: "role", value: second },
          mode: "block",
          reason: "One person should not hold both test roles.",
        });
        expect(rule.status).toBe(201);
        made.rule = rule.body.id;

        // The rule in the console, in words.
        await open(page, "/console/conflicts");
        await expect(page.getByRole("gridcell", { name: ruleName })).toBeVisible();

        // The grant dialog says before the confirm that the rule refuses it; the grant is refused with the rule named.
        await open(page, `/users/${colleagueId}`);
        await page.getByRole("tab", { name: "Access" }).click();
        await page.getByRole("button", { name: "Grant a role" }).click();
        const dialog = page.getByRole("dialog", { name: /^Grant a role to / });
        await dialog.getByRole("combobox", { name: "Role" }).fill(`E2E second side ${stamp}`);
        await page.getByRole("option", { name: `E2E second side ${stamp}` }).click();
        const notice = dialog.getByRole("status").filter({ hasText: "The conflict rules refuse this grant" });
        await expect(notice).toBeVisible();
        await expect(notice).toContainText(`${ruleName}:`);
        await page.screenshot({ path: `test-results/e2e/grant-conflict-${scheme}.png` });
        await dialog.getByRole("button", { name: "Grant role" }).click();
        await expect(dialog.getByRole("alert")).toContainText(`The conflict rule '${ruleName}' blocks this`);
        await page.screenshot({ path: `test-results/e2e/grant-refused-${scheme}.png` });

        // Nothing was granted.
        const assignments = await call<{ data: { id: string; role_id: string }[] }>(page, "GET", `/api/v1/role-assignments?filter=principal_id:eq:${colleagueId}&page_size=100`);
        expect(assignments.body.data.filter((assignment) => assignment.role_id === second)).toEqual([]);
      } finally {
        // Whatever happened above, the colleague keeps no role of this test and the rule goes.
        if (made.roles.length > 0) {
          const assignments = await call<{ data: { id: string; role_id: string }[] }>(page, "GET", `/api/v1/role-assignments?filter=principal_id:eq:${colleagueId}&page_size=100`);
          for (const assignment of assignments.body.data.filter((row) => made.roles.includes(row.role_id))) {
            await call(page, "DELETE", `/api/v1/role-assignments/${assignment.id}`);
          }
        }
        if (made.rule) {
          await call(page, "DELETE", `/api/v1/conflict-rules/${made.rule}`);
        }
        for (const role of made.roles) {
          await call(page, "DELETE", `/api/v1/roles/${role}`);
        }
      }
    });
  }
});
