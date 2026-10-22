import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { administrator, envelope, me, record, roles, stubAccess, units } from "./stubs";

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

const contoso = {
  id: "01a16400-0000-7000-8000-000000000001",
  name: "Contoso Entra ID",
  preset: "entra_id",
  discovery_url:
    "https://login.microsoftonline.com/0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0/v2.0/.well-known/openid-configuration",
  client_id: "6a7b8c9d-iron-acme",
  allowed_domains: ["contoso.example", "contoso-labs.example"],
  email_claim: "email",
  name_claim: "name",
  groups_claim: "groups",
  just_in_time: true,
  default_role_code: roles[0].code,
  default_home_unit_id: units[3].id,
  sso_only: true,
  trust_provider_mfa: true,
  role_mappings: [
    { group: "finance-approvers", role_code: roles[0].code, unit_id: units[3].id, include_children: true },
    { group: "it-readers", role_code: roles[1].code, unit_id: null, include_children: false },
  ],
  created_at: "2026-10-22T09:00:00Z",
  updated_at: "2026-10-22T09:30:00Z",
  version: 2,
};
const fabrikam = {
  ...contoso,
  id: "01a16400-0000-7000-8000-000000000002",
  name: "Fabrikam Okta",
  preset: "okta",
  discovery_url: "https://fabrikam.okta.com/.well-known/openid-configuration",
  allowed_domains: ["fabrikam.example"],
  just_in_time: false,
  sso_only: false,
  trust_provider_mfa: false,
  role_mappings: [],
};

/** "Test connection" as the test provider answers it: every check passed, sign-out included. */
const report = {
  passed: true,
  items: [
    { check: "discovery", status: "passed", message: "Read the discovery document of http://idp.localhost/acme." },
    {
      check: "endpoints",
      status: "passed",
      message:
        "Sign-in page http://idp.localhost/acme/authorize, token endpoint http://idp.localhost/acme/token, keys http://idp.localhost/acme/jwks.",
    },
    { check: "keys", status: "passed", message: "Read 1 signing key." },
    { check: "code_flow", status: "passed", message: "The provider offers the authorization code flow." },
    { check: "pkce", status: "passed", message: "The provider takes PKCE with S256." },
    { check: "signing", status: "passed", message: "ID tokens are signed with RS256, PS256, ES256." },
    {
      check: "end_session",
      status: "warning",
      message: "The provider publishes no end-session endpoint: people sign out here only.",
    },
    {
      check: "client",
      status: "passed",
      message:
        "The client identifier and secret are proved by the first sign-in; the provider must list https://<your address>/sso/callback as a redirect URI.",
    },
  ],
};

/** The console's stubs, an administrator who may manage single sign-on, and the two providers. */
async function stubSso(page: Page): Promise<void> {
  await stubAccess(page);
  const permissions = [...administrator, "identity.sso.manage"];
  await page.route("**/api/v1/me", (route) =>
    route.fulfill({
      json: {
        ...me,
        permissions,
        permissions_anywhere: permissions,
        home_unit: { id: units[0].id, name: units[0].name, code: units[0].code },
      },
    }),
  );
  await page.route("**/api/v1/sso/providers?**", (route) =>
    route.fulfill({ json: envelope(route.request().url(), [contoso, fabrikam]) }),
  );
  await page.route(`**/api/v1/sso/providers/${contoso.id}`, (route) => route.fulfill({ json: contoso }));
  await page.route("**/api/v1/sso/test", (route) => route.fulfill({ json: report }));
}

for (const scheme of schemes) {
  test(`the single sign-on pages in the ${scheme} scheme: the providers, a new one tested against the test provider, and one with its mappings, with no WCAG 2.1 AA violation [TF-014 AC6, TF-172 AC1, ADR-0024, ADR-0048]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubSso(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    await page.goto("/console/sso");
    const grid = page.getByRole("region", { name: "Single sign-on" });
    await expect(grid.locator('.ag-row[row-index="0"]').first()).toContainText("Contoso Entra ID");
    await expect(page.getByRole("link", { name: "Connect a provider" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/sso-providers-${scheme}.png` });
    await expectNoViolation(page);

    // A new provider: the preset fills the discovery address, and the test speaks each check in words.
    await page.goto("/console/sso/new");
    await page
      .getByRole("textbox", { name: "Discovery address" })
      .fill("http://idp.localhost/acme/.well-known/openid-configuration");
    await page.getByRole("button", { name: "Test connection" }).click();
    await expect(page.getByText("The connection works.")).toBeVisible();
    await expect(page.getByText(/^Warning: Sign-out at the provider\./)).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/sso-provider-new-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // An existing provider: its secret is never shown, its groups map to roles at their units.
    await page.goto(`/console/sso/${contoso.id}`);
    await expect(page.getByRole("heading", { name: "Contoso Entra ID" })).toBeVisible();
    await expect(page.getByLabel("Client secret")).toHaveValue("");
    await expect(page.getByRole("group", { name: "Mapping 1" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/sso-provider-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });

  test(`the sign-in page with a company login and a person who must use it, and the record's Sign-in tab, in the ${scheme} scheme, with no WCAG 2.1 AA violation [TF-014 AC2, TF-172 AC1, ADR-0024, ADR-0048]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.route("**/api/**", (route) =>
      route.fulfill({
        status: 401,
        contentType: "application/problem+json",
        body: JSON.stringify({ status: 401, error_code: "auth.not_authenticated" }),
      }),
    );
    await page.route("**/auth/sign-in-options", (route) =>
      route.fulfill({ json: { passkey_sign_in: false, providers: [{ id: contoso.id, name: contoso.name }] } }),
    );
    await page.route("**/auth/login", (route) =>
      route.fulfill({
        status: 401,
        contentType: "application/problem+json",
        body: JSON.stringify({
          status: 401,
          detail: "Use your company login.",
          error_code: "identity.login.use_company_login",
          correlation_id: "0d3c2b1a",
          provider_id: contoso.id,
          provider_name: contoso.name,
        }),
      }),
    );

    await page.goto("/login");
    await expect(page.getByRole("button", { name: "Continue with Contoso Entra ID" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/login-sso-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByLabel(/login identifier/i).fill("ada@contoso.example");
    await page.getByLabel(/^password/i).fill("an old password");
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(page.getByRole("alert")).toContainText("Use your company login.");
    await page.screenshot({ path: `test-results/a11y/login-company-only-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    // The person's record: their company logins and the mark, for an administrator who may change it.
    await page.unrouteAll({ behavior: "ignoreErrors" });
    await stubSso(page);
    await page.route(`**/api/v1/users/${record.id}`, (route) =>
      route.fulfill({
        json: {
          ...record,
          sso_only: true,
          sso_only_by_provider: false,
          sso_links: [
            {
              provider_id: contoso.id,
              provider_name: contoso.name,
              email: "ada@contoso.example",
              linked_at: "2026-10-22T09:00:00Z",
              last_sign_in_at: "2026-10-22T09:45:00Z",
            },
          ],
        },
      }),
    );
    await page.goto(`/users/${record.id}`);
    await page.getByRole("tab", { name: "Sign-in" }).click();
    await expect(page.getByRole("button", { name: "Allow a password again" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/user-sign-in-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);
  });
}
