import { expect, test } from "@playwright/test";
import { virtualAuthenticator } from "./helpers";

// The person of the run who holds the role the tenant requires a second factor of (e2e-data.py, ADR-0047).
const person = { loginId: process.env.E2E_MFA_LOGIN_ID ?? "", password: process.env.E2E_MFA_PASSWORD ?? "" };

test.describe("second factors", () => {
  test("a person who must have a second factor creates a passkey in Chromium's virtual authenticator before any page, then signs in with their password and the passkey [TF-013 AC1, ADR-0047]", async ({
    page,
  }) => {
    test.skip(!person.loginId, "E2E_MFA_LOGIN_ID comes from make e2e's data script");
    const { cdp, authenticatorId } = await virtualAuthenticator(page);
    const shell = page.getByRole("heading", { name: "IronSaaS", exact: true });

    await page.goto("/login");
    await page.getByLabel(/login identifier/i).fill(person.loginId);
    await page.getByLabel(/^password/i).fill(person.password);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();

    // Enrolment, before any page: the password made no session.
    await expect(page.getByRole("heading", { name: "Set up a second factor" })).toBeVisible();
    const me = await page.evaluate(async () => (await fetch("/api/v1/me")).status);
    expect(me).toBe(401);
    await page.getByText("Passkey", { exact: true }).click();
    await page.getByLabel(/^Name/).fill("Virtual key");
    await page.getByRole("button", { name: "Create a passkey" }).click();

    // The recovery codes, once, before the shell.
    await expect(page.getByRole("heading", { name: "Save your recovery codes" })).toBeVisible();
    await expect(page.getByRole("list", { name: "Your recovery codes" }).getByRole("listitem")).toHaveCount(10);
    await page.screenshot({ path: "test-results/e2e/passkey-recovery-codes.png", fullPage: true });
    await page.getByLabel("I have saved my recovery codes").check();
    await page.getByRole("button", { name: "Continue" }).click();
    await expect(shell).toBeVisible();

    const { credentials } = await cdp.send("WebAuthn.getCredentials", { authenticatorId });
    expect(credentials).toHaveLength(1);
    expect(credentials[0]?.isResidentCredential).toBe(true);

    // Signed out, the password alone no longer signs them in: the second step, and the passkey answers it.
    await page.getByRole("button", { name: /signed in as/i }).click();
    await page.getByRole("menuitem", { name: /sign out/i }).click();
    await expect(page).toHaveURL(/\/login/);
    await page.getByLabel(/login identifier/i).fill(person.loginId);
    await page.getByLabel(/^password/i).fill(person.password);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Confirm it is you" })).toBeVisible();
    await page.screenshot({ path: "test-results/e2e/passkey-second-step.png", fullPage: true });
    await page.getByRole("button", { name: "Use your passkey" }).click();
    await expect(shell).toBeVisible();

    // My profile lists the passkey by the name the person gave it, used just now.
    await page.goto("/me?tab=security");
    const factors = page.getByRole("table", { name: "Your second factors" });
    await expect(factors).toContainText("Virtual key");
    await expect(factors).toContainText("Passkey");
    await expect(factors).not.toContainText("Not yet");
    await page.screenshot({ path: "test-results/e2e/passkey-profile.png", fullPage: true });
  });
});
