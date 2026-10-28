import { expect, test } from "@playwright/test";
import { call, open, signIn } from "./helpers";

/** What the page reads from GET /api/v1/keys, as much as the test checks. */
interface Keys {
  keys: { key: string; version: number }[];
  rotations: { key: string; reason: string }[];
}

/**
 * ADR-0054 on a real unit: the console's Keys page shows the tenant's keys as the unit last heard them from the control
 * plane, read only, with the latest rotations, among them the smoke's index key rotation, which make smoke runs first.
 * The tenant's rotation policy is in Settings, under Keys. Nothing is changed here: rotation is the operator's.
 */
test.describe("the tenant's keys", () => {
  for (const scheme of ["light", "dark"] as const) {
    test(`the Keys page shows each key's version and the latest rotations with who asked and why, as the unit holds them, in the ${scheme} scheme [ADR-0054]`, async ({
      page,
    }) => {
      await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
      await signIn(page);
      const answer = await call<Keys>(page, "GET", "/api/v1/keys");
      expect(answer.status).toBe(200);
      const index = answer.body.keys.find((key) => key.key === "index");
      expect(index, "the unit knows its index key once the control plane answered").toBeDefined();

      await open(page, "/console/keys");
      await expect(page.getByRole("heading", { level: 2, name: "Keys" })).toBeVisible();
      const current = page.getByRole("table", { name: "The keys" });
      for (const name of ["Data key", "Index key", "Signing key", "Database password"]) {
        await expect(current.getByRole("row", { name: new RegExp(name) })).toBeVisible();
      }
      await expect(current.getByRole("row", { name: /Index key/ })).toContainText(`Version ${index!.version}`);

      const smoke = answer.body.rotations.find(
        (rotation) => rotation.key === "index" && rotation.reason.startsWith("Smoke run"),
      );
      if (smoke !== undefined) {
        const rotations = page.getByRole("table", { name: "Latest rotations" });
        await expect(rotations.getByRole("row").filter({ hasText: smoke.reason }).first()).toBeVisible();
      }
      // Read only: no control on the page changes a key.
      await expect(page.getByRole("main").getByRole("button")).toHaveCount(0);
      await page.screenshot({ path: `test-results/e2e/keys-${scheme}.png`, fullPage: true });

      // The tenant's policy for its data and index keys, within the operator's bounds, is a setting.
      await open(page, "/console/settings");
      const policy = page.getByRole("article", { name: "The data and index keys rotate every" });
      await policy.scrollIntoViewIfNeeded();
      await expect(policy).toContainText(/\d+ days/);
      await policy.screenshot({ path: `test-results/e2e/settings-keys-${scheme}.png` });
    });
  }
});
