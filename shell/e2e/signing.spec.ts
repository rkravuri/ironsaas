import { expect, test, type Page } from "@playwright/test";
import { call, loginId, open, password, signIn } from "./helpers";

/**
 * Electronic signatures against a real unit (S3 T7, TF-063, TF-219, ADR-0051). The administrator signs an access review
 * decision while the tenant's policy asks for it, with the first signature's components: the login identifier, the
 * password and a second factor, their passkey. Then they record a review of the audit trail, always signed, and print
 * it: the signature block is on paper with the name, the time with its zone and the meaning. The policy is set back,
 * and the campaign closed, whatever happens, so the next run starts clean.
 */
const childUnitId = process.env.E2E_CHILD_UNIT_ID ?? "";
const policiesKey = "compliance.signing.policies";
const run = Date.now().toString(36);

const day = (offset: number) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);

interface Item {
  id: string;
  principal_name: string;
  role_name: string;
  yours: boolean;
  decision: { decision_id?: string | null; signature?: { meaning: string } | null } | null;
}

/** The signing dialog's first signature: who they are, their password, and their passkey as the second factor. */
async function signFirst(page: Page, title: string): Promise<void> {
  const dialog = page.getByRole("dialog", { name: title });
  await expect(dialog.getByText(/first signature since you signed in/)).toBeVisible();
  await dialog.getByLabel(/^Login identifier/).fill(loginId);
  await dialog.getByLabel(/^Password/).fill(password);
  await dialog.getByRole("button", { name: "Sign with your passkey" }).click();
  await expect(dialog).toBeHidden();
}

test.describe("electronic signatures", () => {
  test("the administrator signs an access review decision the policy asks a signature for, approved, with the components of a first signature [TF-063 AC1, TF-063 AC2, TF-063 AC4, ADR-0051]", async ({
    page,
  }) => {
    let reviewId: string | undefined;
    await signIn(page);
    try {
      const policy = await call(page, "PUT", `/api/v1/settings/${policiesKey}`, {
        scope: "tenant",
        unit_id: null,
        value: { access_review_decision: { required: true, meanings: ["approved", "rejected"], reauthenticate: true } },
      });
      expect(policy.status).toBe(200);
      const started = await call<{ id: string }>(page, "POST", "/api/v1/access-reviews", {
        name: `E2E signed review ${run}`,
        due_on: day(7),
        units: [{ unit_id: childUnitId, include_children: true }],
      });
      expect(started.status).toBe(201);
      reviewId = started.body.id;
      const items = await call<{ data: Item[] }>(
        page,
        "GET",
        `/api/v1/access-reviews/${reviewId}/items?state=open&page_size=50`,
      );
      const item = items.body.data.find((candidate) => !candidate.yours && candidate.decision === null);
      expect(item, "the campaign has an item for the administrator to decide").toBeDefined();
      if (!item) {
        return;
      }

      await open(page, `/console/access-reviews/${reviewId}`);
      await expect(page.locator("[data-signed-decisions]")).toBeVisible();
      await expect(page.getByRole("button", { name: /^Confirm the \d+ items? on this page$/ })).toBeDisabled();
      await page.getByRole("gridcell", { name: item.principal_name }).first().click();
      await page.getByRole("button", { name: "Confirm", exact: true }).click();
      const dialog = page.getByRole("dialog", { name: "Sign the confirmation" });
      await expect(dialog).toContainText("Approved");
      await dialog.getByLabel(/^Comment/).fill(`Still needed, e2e run ${run}.`);
      await page.screenshot({ path: "test-results/e2e/signing-dialog.png" });
      await signFirst(page, "Sign the confirmation");
      await expect(page.getByText(`Confirmed: ${item.principal_name} keeps ${item.role_name}.`)).toBeVisible();
      await page.screenshot({ path: "test-results/e2e/review-signed.png", fullPage: true });

      const decided = await call<{ data: Item[] }>(
        page,
        "GET",
        `/api/v1/access-reviews/${reviewId}/items?state=decided&page_size=50`,
      );
      const signedItem = decided.body.data.find((candidate) => candidate.id === item.id);
      expect(signedItem?.decision?.signature?.meaning).toBe("approved");
      const signatures = await call<{
        data: {
          meaning: string;
          method: string;
          signer_login_id: string;
          hash_matches: boolean;
          record_changed: boolean;
          comment: string;
        }[];
      }>(
        page,
        "GET",
        `/api/v1/signatures?record_type=access_review_decision&record_id=${signedItem?.decision?.decision_id ?? ""}`,
      );
      expect(signatures.body.data).toEqual([
        expect.objectContaining({
          meaning: "approved",
          method: "password_and_passkey",
          signer_login_id: loginId,
          hash_matches: true,
          record_changed: false,
          comment: `Still needed, e2e run ${run}.`,
        }),
      ]);
    } finally {
      await call(page, "DELETE", `/api/v1/settings/${policiesKey}?scope=tenant`);
      if (reviewId) {
        await call(page, "POST", `/api/v1/access-reviews/${reviewId}/close`);
      }
    }
  });

  test("the administrator records a review of the audit trail, signs it reviewed, and prints it with its signature block [TF-219 AC1, TF-219 AC3, TF-065 AC2, ADR-0051]", async ({
    page,
  }) => {
    await signIn(page);
    const me = await call<{ display_name: string }>(page, "GET", "/api/v1/me");

    await open(page, "/compliance/audit-reviews");
    await expect(page.getByRole("region", { name: "The last twelve months" })).toBeVisible();
    await page.getByRole("button", { name: "Record a review" }).click();
    const form = page.getByRole("dialog", { name: "Record a review of the audit trail" });
    await form.getByLabel("From", { exact: true }).fill(day(-8));
    await form.getByLabel("To", { exact: true }).fill(day(-1));
    await form.getByRole("button", { name: "Add a search" }).click();
    await form.getByLabel(/what it looked for/).fill(`Failed sign-ins, e2e run ${run}`);
    await form.getByLabel(/Search 1: address/).fill("/audit?event_type=identity.login_failed");
    await form.getByRole("button", { name: "Continue to sign" }).click();
    await signFirst(page, "Sign the review");

    await expect(page.getByRole("heading", { name: /^Audit-trail review, / })).toBeVisible();
    const block = page.getByRole("region", { name: "Signatures" });
    await expect(block).toContainText(me.body.display_name);
    await expect(block).toContainText("Reviewed");
    await expect(block).toContainText(/(GMT|UTC)/);
    await expect(block).toContainText("The signed content's hash matches the snapshot kept with the signature.");

    // On paper: the record and its signature block, without the frame.
    await page.emulateMedia({ media: "print" });
    await expect(page.getByRole("navigation", { name: "Main navigation" })).toBeHidden();
    await expect(page.getByRole("button", { name: "Print" })).toBeHidden();
    await expect(block).toBeVisible();
    await expect(block).toContainText(me.body.display_name);
    await page.screenshot({ path: "test-results/e2e/audit-review-printed.png", fullPage: true });
    await page.pdf({ path: "test-results/e2e/audit-review-printed.pdf", format: "A4" });
  });
});
