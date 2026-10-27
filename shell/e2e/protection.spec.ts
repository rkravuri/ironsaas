import { expect, test, type Page } from "@playwright/test";
import { call, open, signIn } from "./helpers";

const run = process.env.E2E_RUN ?? Date.now().toString(36);

/** Another site, as a browser sees it: the single sign-on test provider's origin, which the stack serves (ADR-0048). */
const otherSite = "http://idp.localhost";
const originsKey = "protection.cors.allowed_origins";

/** What a page of the other site gets when it calls the tenant's API with credentials: a status, or refused by the browser. */
async function callFrom(other: Page, url: string): Promise<number | "refused"> {
  return other.evaluate(async (target) => {
    try {
      return (await fetch(target, { credentials: "include" })).status;
    } catch {
      return "refused" as const;
    }
  }, url);
}

async function setOrigins(page: Page, origins: string[]): Promise<void> {
  const saved = await call(page, "PUT", `/api/v1/settings/${originsKey}`, {
    scope: "tenant",
    unit_id: null,
    value: origins,
  });
  expect(saved.status).toBe(200);
}

test.describe("the web surface's protection", () => {
  test("a page of another site may call the API only once the tenant lists its origin, and not after it is removed [TF-070 AC2, ADR-0053]", async ({
    page,
    context,
  }) => {
    // Three polls of the settings cache, up to 45 seconds each.
    test.setTimeout(240_000);
    await signIn(page);
    const api = `${new URL(page.url()).origin}/api/v1/me`;
    const other = await context.newPage();
    await other.goto(`${otherSite}/e2e-${run}/.well-known/openid-configuration`);

    // Nobody's origin is listed by default: the browser withholds the answer from the other site's page.
    await setOrigins(page, []);
    await expect.poll(() => callFrom(other, api), { timeout: 45_000 }).toBe("refused");

    try {
      await setOrigins(page, [otherSite]);
      // Listed, the call is answered: 401, as the session's cookie is never sent to another site's page.
      await expect.poll(() => callFrom(other, api), { timeout: 45_000 }).toBe(401);
    } finally {
      await setOrigins(page, []);
    }

    await expect.poll(() => callFrom(other, api), { timeout: 45_000 }).toBe("refused");
  });

  test("after repeated failures the login asks for the check, solves it in this browser asking no other host, and checks the password again [TF-072 AC4, ADR-0053]", async ({
    page,
  }) => {
    test.setTimeout(300_000);
    const hosts = new Set<string>();
    page.on("request", (request) => hosts.add(new URL(request.url()).host));
    await page.goto("/login");
    const tenant = new URL(page.url()).host;
    await page.getByLabel(/login identifier/i).fill(`nobody-${run}@${tenant}`);
    await page.getByLabel(/^password/i).fill("not the password");

    const check = page.getByRole("checkbox", { name: /i am a person/i });
    const alert = page.getByRole("alert");
    const limited = alert.filter({ hasText: /Too many requests in a short time/ });
    // Failures from this address, these and any earlier ones within 15 minutes, until the form asks for the check.
    for (let attempt = 1; attempt <= 12 && !(await check.isVisible()); attempt++) {
      await page.getByRole("button", { name: "Sign in", exact: true }).click();
      await expect(alert.or(check).first()).toBeVisible();
      if (await limited.isVisible()) {
        const [, count] = /Wait (\d+) second/.exec(await limited.innerText()) ?? ["", "30"];
        await page.waitForTimeout((Number(count) + 1) * 1000);
      }
    }

    await expect(check).toBeVisible();
    await expect(alert).toContainText(
      "Several sign-ins failed from here or for this login. Tick the box below, then sign in.",
    );
    await page.screenshot({ path: "test-results/e2e/login-check.png", fullPage: true });

    await check.check();
    await expect(page.getByText("Checked. Select Sign in.")).toBeVisible({ timeout: 120_000 });
    await page.getByRole("button", { name: "Sign in", exact: true }).click();

    // The answer passed and the password was checked: wrong, as every failure says. A new attempt needs the box again.
    await expect(alert.filter({ hasText: "The login details are not correct." }).or(limited)).toBeVisible();
    await expect(check).not.toBeChecked();
    expect([...hosts].filter((host) => host !== tenant)).toEqual([]);
  });

  test("the shell's page runs no inline code: its own styles carry the page's nonce, and an injected script is blocked and reported [TF-070 AC1, TF-070 AC3, ADR-0053]", async ({
    page,
  }) => {
    const violations: string[] = [];
    page.on("console", (message) => {
      if (message.type() === "error" && /Content Security Policy/i.test(message.text())) {
        violations.push(message.text());
      }
    });
    await signIn(page);
    await open(page, "/users");
    await expect(page.getByRole("grid").first()).toBeVisible();
    expect(violations).toEqual([]);

    const injected = await page.evaluate(
      () =>
        new Promise<{ ran: boolean; directive: string }>((resolve) => {
          const marker = `__injected_${Math.random().toString(36).slice(2)}`;
          document.addEventListener(
            "securitypolicyviolation",
            (event) => {
              resolve({
                ran: (window as unknown as Record<string, unknown>)[marker] === true,
                directive: event.effectiveDirective,
              });
            },
            { once: true },
          );
          const script = document.createElement("script");
          script.textContent = `window.${marker} = true;`;
          document.head.append(script);
        }),
    );

    expect(injected).toEqual({ ran: false, directive: "script-src-elem" });
    await expect
      .poll(
        async () =>
          (await call<{ data: { directive: string }[] }>(page, "GET", "/api/v1/csp-reports")).body.data.some(
            (report) => report.directive === "script-src-elem",
          ),
        { timeout: 30_000 },
      )
      .toBe(true);
  });
});
