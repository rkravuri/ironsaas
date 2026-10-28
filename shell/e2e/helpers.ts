import { createHmac } from "node:crypto";
import { expect, test, type CDPSession, type Locator, type Page } from "@playwright/test";

export const loginId = process.env.E2E_LOGIN_ID ?? "admin@smoke.localhost";
export const password = process.env.E2E_PASSWORD ?? "";

/**
 * Signs in through the real login page and waits for the shell: the administrator, unless another account is given.
 * The administrator holds a second factor (ADR-0047): the authenticator app the smoke enrolled, whose key make e2e reads
 * from the store, and, from their first sign-in in a worker, a passkey in Chromium's virtual authenticator, which this
 * helper adds then and gives to every later page. A passkey answers at once, where each code of the app must be of a
 * later 30-second step than the one before.
 */
export async function signIn(
  page: Page,
  account: { loginId: string; password: string } = { loginId, password },
): Promise<void> {
  if (!account.password) {
    throw new Error("E2E_PASSWORD is required: the administrator's password, as the smoke tool kept it in the store.");
  }
  const administrator = account.loginId === loginId;
  const authenticator = administrator ? await virtualAuthenticator(page) : undefined;
  if (authenticator && administratorPasskey) {
    await authenticator.cdp.send("WebAuthn.clearCredentials", { authenticatorId: authenticator.authenticatorId });
    await authenticator.cdp.send("WebAuthn.addCredential", {
      authenticatorId: authenticator.authenticatorId,
      credential: administratorPasskey,
    });
  }
  await page.goto("/login");
  await page.getByLabel(/login identifier/i).fill(account.loginId);
  await page.getByLabel(/^password/i).fill(account.password);
  // Exact: the home page's group of Foundation openers is headed "IronSaaS Foundation" (ADR-0043).
  const shell = page.getByRole("heading", { name: "IronSaaS", exact: true });
  const crash = page.getByRole("heading", { name: /something went wrong/i });
  const second = page.getByRole("heading", { name: "Confirm it is you" });
  const enrol = page.getByRole("heading", { name: "Set up a second factor" });
  await submitSignIn(page, shell.or(crash).or(second).or(enrol));
  if (await enrol.isVisible()) {
    throw new Error(
      `${account.loginId} must enrol a second factor first: run make smoke, which enrols the administrator's.`,
    );
  }
  if (await second.isVisible()) {
    if (!authenticator) {
      throw new Error(
        `${account.loginId} must give a second factor, which the browser tests hold for the administrator only.`,
      );
    }
    const passkey = page.getByRole("button", { name: "Use your passkey" });
    if (administratorPasskey && (await passkey.isVisible())) {
      await passkey.click();
    } else {
      await typeCode(page);
    }
  }
  await expect(shell.or(crash)).toBeVisible();
  if (await crash.isVisible()) {
    throw new Error(`The shell crashed after login: ${await page.getByRole("alert").innerText()}`);
  }
  if (authenticator) {
    if (administratorPasskey) {
      await keepPasskey(authenticator);
    } else {
      await addAdministratorPasskey(page, authenticator);
    }
  }
}

/**
 * Selects Sign in on the login form until the page shows one of the outcomes (TF-072, ADR-0053). The tests stay within
 * the rate limits by waiting, never by raising them: a refusal that says how long to wait is waited out. After repeated
 * failed sign-ins from this machine (the passwords and lockout tests make some), the form asks for the check first: it
 * is ticked, solved in the browser, and the sign-in is sent again.
 */
export async function submitSignIn(page: Page, outcome: Locator): Promise<void> {
  const reached = outcome.first();
  const check = page.getByRole("checkbox", { name: /i am a person/i });
  const limited = page.getByRole("alert").filter({ hasText: /Too many requests in a short time/ });
  for (let attempt = 1; ; attempt++) {
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(outcome.or(limited).or(check).first()).toBeVisible();
    if (attempt < 8 && (await limited.isVisible())) {
      const [, count, unit] = /Wait (\d+) (second|minute)/.exec(await limited.innerText()) ?? ["", "30", "second"];
      const wait = (Number(count) * (unit === "minute" ? 60 : 1) + 1) * 1000;
      // The wait is the limit's, not the test's: the test gets that much more time.
      test.info().setTimeout(test.info().timeout + wait);
      await page.waitForTimeout(wait);
      continue;
    }
    if (attempt < 8 && !(await reached.isVisible()) && (await check.isVisible()) && !(await check.isChecked())) {
      // The work grows with the failures from this address: the test gets the time the browser counts for.
      test.info().setTimeout(test.info().timeout + 120_000);
      await check.check();
      await expect(page.getByText("Checked. Select Sign in.")).toBeVisible({ timeout: 120_000 });
      continue;
    }
    await expect(reached).toBeVisible();
    return;
  }
}

/** A credential of Chromium's virtual authenticator, as CDP gives and takes it: its private key travels with it. */
interface VirtualCredential {
  credentialId: string;
  isResidentCredential: boolean;
  rpId?: string;
  privateKey: string;
  userHandle?: string;
  signCount: number;
}

interface VirtualAuthenticator {
  cdp: CDPSession;
  authenticatorId: string;
}

/** The administrator's passkey in this worker, with the signature counter its last use left. */
let administratorPasskey: VirtualCredential | undefined;
const authenticators = new WeakMap<Page, VirtualAuthenticator>();

/**
 * Chromium's virtual authenticator for the page (ADR-0047): a platform authenticator that keeps discoverable passkeys,
 * verifies its user and answers without a person, as a fingerprint reader would after a touch.
 */
export async function virtualAuthenticator(page: Page): Promise<VirtualAuthenticator> {
  const known = authenticators.get(page);
  if (known) {
    return known;
  }
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("WebAuthn.enable");
  const { authenticatorId } = await cdp.send("WebAuthn.addVirtualAuthenticator", {
    options: {
      protocol: "ctap2",
      transport: "internal",
      hasResidentKey: true,
      hasUserVerification: true,
      isUserVerified: true,
      automaticPresenceSimulation: true,
    },
  });
  const created = { cdp, authenticatorId };
  authenticators.set(page, created);
  return created;
}

/** The administrator's first sign-in in this worker: a passkey added in My profile, kept for every later page. */
async function addAdministratorPasskey(page: Page, authenticator: VirtualAuthenticator): Promise<void> {
  await page.goto("/me?tab=security");
  await page.getByRole("button", { name: "Add a second factor" }).click();
  const dialog = page.getByRole("dialog", { name: "Add a second factor" });
  const choice = dialog.getByText("Passkey", { exact: true });
  if (await choice.isVisible()) {
    await choice.click();
  }
  await dialog.getByLabel(/^Name/).fill("Browser tests");
  await dialog.getByRole("button", { name: "Create a passkey" }).click();
  await expect(page.getByText("Your second factor is saved.")).toBeVisible();
  // A first factor brings its recovery codes, shown once; the administrator's app came first, so normally none show.
  const saved = page.getByLabel("I have saved my recovery codes");
  if (await saved.isVisible()) {
    await saved.check();
    await page.getByRole("button", { name: "Done" }).click();
  }
  const { credentials } = await authenticator.cdp.send("WebAuthn.getCredentials", {
    authenticatorId: authenticator.authenticatorId,
  });
  administratorPasskey = credentials[0];
  // The administrator keeps their factors from run to run (the smoke resets them only when its key fails), so the passkeys
  // earlier runs added, whose private keys left with those runs, go: the newest is this run's.
  const listed = await call<{ factors: { id: string; kind: string; name: string; created_at: string }[] }>(
    page,
    "GET",
    "/api/v1/me/factors",
  );
  const ours = listed.body.factors.filter((factor) => factor.kind === "passkey" && factor.name === "Browser tests");
  const newest = ours.reduce<string | undefined>(
    (latest, factor) => (latest === undefined || factor.created_at > latest ? factor.created_at : latest),
    undefined,
  );
  for (const earlier of ours.filter((factor) => factor.created_at !== newest)) {
    await call(page, "DELETE", `/api/v1/me/factors/${earlier.id}`);
  }
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "IronSaaS", exact: true })).toBeVisible();
}

/** The counter grows with every signature, and the unit refuses one that does not: the next page starts from this one. */
async function keepPasskey(authenticator: VirtualAuthenticator): Promise<void> {
  const { credentials } = await authenticator.cdp.send("WebAuthn.getCredentials", {
    authenticatorId: authenticator.authenticatorId,
  });
  administratorPasskey =
    credentials.find((credential) => credential.credentialId === administratorPasskey?.credentialId) ??
    administratorPasskey;
}

/**
 * Keeps the administrator's passkey as the page's authenticator left it, after a use outside the sign-in, such as a
 * signature (ADR-0051): the server refuses a counter that does not grow, as a cloned key's would, so the next page's
 * sign-in must start from this one.
 */
export async function rememberPasskey(page: Page): Promise<void> {
  const authenticator = authenticators.get(page);
  if (authenticator) {
    await keepPasskey(authenticator);
  }
}

const totpSecret = process.env.E2E_TOTP_SECRET ?? "";
let lastStep = -1;

/** RFC 6238's code for a step of the administrator's authenticator app: HMAC-SHA1, six digits. */
function totp(step: number): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  const bytes: number[] = [];
  let buffer = 0;
  let bits = 0;
  for (const character of totpSecret.toUpperCase().replace(/[\s=-]/g, "")) {
    buffer = ((buffer << 5) | alphabet.indexOf(character)) & 0xffff;
    bits += 5;
    if (bits >= 8) {
      bytes.push((buffer >> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  const counter = Buffer.alloc(8);
  counter.writeBigInt64BE(BigInt(step));
  const digest = createHmac("sha1", Buffer.from(bytes)).update(counter).digest();
  const offset = digest[digest.length - 1] & 0x0f;
  return String((digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

/** A code of a later step than the last one used, the next step ahead when the current one is used, waiting when both are. */
async function nextCode(): Promise<string> {
  for (;;) {
    const now = Math.floor(Date.now() / 1000);
    const current = Math.floor(now / 30);
    const step = Math.max(current, lastStep + 1);
    if (step <= current + 1) {
      lastStep = step;
      return totp(step);
    }
    await new Promise((resolve) => setTimeout(resolve, (30 - (now % 30) + 1) * 1000));
  }
}

/**
 * The second step with the app's code. A code of a step the smoke or the run's data script used is refused as any wrong
 * code is: the next step's then, at most three tries, under the three wrong codes that start a cooling period.
 */
async function typeCode(page: Page): Promise<void> {
  if (!totpSecret) {
    throw new Error(
      "E2E_TOTP_SECRET is required: the key of the administrator's authenticator app, as the smoke kept it in the store.",
    );
  }
  const shell = page.getByRole("heading", { name: "IronSaaS", exact: true });
  for (let attempt = 1; ; attempt++) {
    await page.getByLabel(/code from your authenticator app/i).fill(await nextCode());
    await page.getByRole("button", { name: "Continue" }).click();
    const refused = page.getByRole("alert");
    await expect(shell.or(refused)).toBeVisible();
    if (await shell.isVisible()) {
      return;
    }
    if (attempt >= 3 || !(await refused.innerText()).includes("did not prove it is you")) {
      throw new Error(`The administrator's code was refused: ${await refused.innerText()}`);
    }
  }
}

/** Opens a page of the shell and fails with the error text if the page crashed instead of rendering. */
export async function open(page: Page, path: string): Promise<void> {
  await page.goto(path);
  const crash = page.getByRole("heading", { name: /something went wrong/i });
  await page.waitForLoadState("networkidle");
  if (await crash.isVisible()) {
    throw new Error(`${path} crashed: ${await page.getByRole("alert").innerText()}`);
  }
}

export interface Answer<T> {
  status: number;
  body: T;
}

/** A call from the page, with its cookie: Node's resolver behind page.request does not know *.localhost, Chromium does. */
export async function call<T>(page: Page, method: string, path: string, body?: unknown): Promise<Answer<T>> {
  return page.evaluate(
    async ({ method, path, body }) => {
      const headers: Record<string, string> = { Accept: "application/json" };
      const init: RequestInit = { method, headers };
      if (body !== undefined) {
        headers["Content-Type"] = "application/json";
        init.body = JSON.stringify(body);
      }
      const response = await fetch(path, init);
      const text = await response.text();
      return { status: response.status, body: (text ? JSON.parse(text) : null) as never };
    },
    { method, path, body },
  );
}
