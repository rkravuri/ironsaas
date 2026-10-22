import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { principals, recentFor, record, sessionFromNow, stubAccess, stubHome, users, type Who } from "./stubs";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];
const people: { who: Who; label: string; openers: string[] }[] = [
  { who: "administrator", label: "tenant administrator", openers: ["Identity", "Console"] },
  { who: "child", label: "child-unit administrator", openers: ["Identity"] },
];

async function expectNoViolation(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
  expect(
    results.violations.map(
      (violation) =>
        `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => node.target.map(String).join(" ")).join("; ")}`,
    ),
  ).toEqual([]);
}

/** Opens the shell as one of the home page's people, with the pages it opened before, in a scheme and with reduced motion. */
async function open(
  page: Page,
  who: Who,
  scheme: (typeof schemes)[number],
  path = "/",
  session = sessionFromNow(),
): Promise<void> {
  await page.setViewportSize({ width: 1280, height: 800 });
  await stubHome(page, who, session);
  if (who === "administrator") {
    const recent = recentFor(who);
    await page.addInitScript(([key, pages]) => window.localStorage.setItem(key, pages), [
      recent.key,
      JSON.stringify(recent.pages),
    ] as const);
  }
  await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
  await page.goto(path);
}

for (const scheme of schemes) {
  for (const person of people) {
    test(`the home page of a ${person.label} in the ${scheme} scheme shows what it may open and has no WCAG 2.1 AA violation [TF-172 AC1, ADR-0033, ADR-0043]`, async ({
      page,
    }) => {
      await open(page, person.who, scheme);

      await expect(page.getByRole("heading", { level: 1 })).toHaveText(
        new RegExp(`^Good (morning|afternoon|evening), ${principals[person.who].display_name}$`),
      );
      await expect(page.getByRole("img", { name: /A small building with its flag up/ })).toBeVisible();
      await expect(page.getByRole("article")).toHaveCount(person.openers.length);
      // Every module here is the Foundation's, so every opener sits in its group, the first (ADR-0043).
      const foundation = page.getByRole("region", { name: "IronSaaS Foundation" });
      await expect(foundation.getByRole("article")).toHaveCount(person.openers.length);
      for (const name of person.openers) {
        await expect(foundation.getByRole("article", { name })).toBeVisible();
      }
      const actions = page.getByRole("region", { name: "Quick actions" });
      if (person.who === "administrator") {
        await expect(actions.getByRole("link")).toHaveText(["Invite a person", "Add a unit"]);
        await expect(page.getByRole("region", { name: "Recently opened" }).getByRole("link")).toHaveText([
          users[1].display_name,
          "Organisation units",
          "Users",
        ]);
      } else {
        await expect(actions).toHaveCount(0);
        await expect(page.getByRole("region", { name: "Recently opened" })).toContainText("will appear here");
      }
      // Home is first in the navigation and is the page shown.
      await expect(
        page.getByRole("navigation", { name: "Main navigation" }).getByRole("link").first(),
      ).toHaveAccessibleName("Home");
      await page.screenshot({ path: `test-results/a11y/home-${person.who}-${scheme}.png`, fullPage: true });
      await expectNoViolation(page);
    });
  }

  test(`the search in the ${scheme} scheme: Ctrl+K opens it, Escape closes it with focus back, Enter opens the match, and no WCAG 2.1 AA violation [TF-172 AC1, ADR-0033]`, async ({
    page,
  }) => {
    await open(page, "administrator", scheme);
    const help = page.getByRole("button", { name: "Help and keyboard shortcuts" });
    await help.focus();

    await page.keyboard.press("Control+K");
    const search = page.getByRole("combobox", { name: "Search pages and actions" });
    await expect(search).toBeFocused();
    await expect(page.getByRole("option", { name: /^Add a unit/ })).toBeVisible();
    await search.fill("prof");
    // The first match is chosen, and the input points a screen reader at it: what Enter will open is heard, not only seen.
    const profile = page.getByRole("option", { name: /^My profile/ });
    await expect(profile).toHaveAttribute("aria-selected", "true");
    await expect(search).toHaveAttribute("aria-activedescendant", (await profile.getAttribute("id")) ?? "");
    await page.screenshot({ path: `test-results/a11y/search-${scheme}.png` });
    await expectNoViolation(page);

    // The arrows move the choice, and the input points at the new one.
    await search.fill("");
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("ArrowDown");
    const chosen = page.getByRole("option", { selected: true });
    await expect(chosen).toHaveCount(1);
    await expect(search).toHaveAttribute("aria-activedescendant", (await chosen.getAttribute("id")) ?? "");
    await expect(chosen).not.toHaveAccessibleName(/^Home/);

    await page.keyboard.press("Escape");
    await expect(search).toBeHidden();
    await expect(help).toBeFocused();

    await page.keyboard.press("Control+K");
    await search.fill("profile");
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/me$/);
  });

  test(`help in the ${scheme} scheme lists the shell's keys and the page's, and has no WCAG 2.1 AA violation [TF-172 AC1, ADR-0033]`, async ({
    page,
  }) => {
    await open(page, "administrator", scheme, "/me");
    await page.getByRole("button", { name: "Help and keyboard shortcuts" }).click();
    const panel = page.getByRole("dialog", { name: "Help" });
    await expect(panel.getByRole("table", { name: "Keys on every page" })).toBeVisible();
    await expect(panel.getByRole("table", { name: "Keys on My profile" })).toContainText("Move between tabs");
    await page.screenshot({ path: `test-results/a11y/help-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");
    await expect(panel).toBeHidden();
  });

  test(`the session warning in the ${scheme} scheme takes focus, announces itself, and has no WCAG 2.1 AA violation [TF-172 AC1, ADR-0033]`, async ({
    page,
  }) => {
    await open(page, "administrator", scheme, "/", sessionFromNow({ idleIn: 100_000 }));
    const warning = page.getByRole("alertdialog", { name: "Your session is about to end" });
    await expect(warning).toBeVisible();
    await expect(warning.getByRole("button", { name: "Stay signed in" })).toBeFocused();
    await expect(warning).toHaveAccessibleDescription(/This one ends at .+ Stay signed in to carry on/);
    await page.screenshot({ path: `test-results/a11y/session-warning-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`the final session warning in the ${scheme} scheme says the session cannot be extended, with no WCAG 2.1 AA violation [TF-172 AC1, ADR-0033]`, async ({
    page,
  }) => {
    await open(page, "administrator", scheme, "/", sessionFromNow({ endsIn: 100_000 }));
    const warning = page.getByRole("alertdialog", { name: "This session cannot be extended" });
    await expect(warning).toBeVisible();
    await expect(warning).toHaveAccessibleDescription(/save your work, then sign in again/);
    await expect(warning.getByRole("button", { name: "Stay signed in" })).toHaveCount(0);
    await page.screenshot({ path: `test-results/a11y/session-final-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`My profile in the ${scheme} scheme sits on the record template, its tabs by keyboard, with no WCAG 2.1 AA violation [TF-172 AC1, ADR-0024, ADR-0027, ADR-0036, ADR-0037]`, async ({
    page,
  }) => {
    await open(page, "child", scheme, "/me");
    await expect(page.getByRole("heading", { level: 2, name: "Grace Hopper" })).toBeVisible();
    await expect(page.locator("[data-panel=record-header]")).toContainText("Home unit: North");
    await page.screenshot({ path: `test-results/a11y/profile-${scheme}.png` });
    await expectNoViolation(page);

    await page.getByRole("tab", { name: "Details" }).focus();
    await page.keyboard.press("ArrowRight");
    await expect(page.getByRole("list", { name: "Your roles" })).toContainText(
      "User reader, at Acme Corporation / Operations / North and every unit below it",
    );
    await page.screenshot({ path: `test-results/a11y/profile-access-${scheme}.png` });
    await expectNoViolation(page);

    // Delegations (A7): the person's own, both ways, in words with their status.
    await page.keyboard.press("ArrowRight");
    await expect(page.getByRole("list", { name: "Your delegations" })).toContainText(
      "You hand user administration to Linus Pauling",
    );
    await expect(page.getByRole("tabpanel", { name: "Delegations" })).toContainText("Over");
    await page.screenshot({ path: `test-results/a11y/profile-delegations-${scheme}.png` });
    await expectNoViolation(page);

    // Preferences (A6): the colour scheme and the density, which follow the person.
    await page.keyboard.press("ArrowRight");
    const preferences = page.getByRole("tabpanel", { name: "Preferences" });
    await expect(preferences).toContainText("they follow you to every browser you sign in on");
    await expect(preferences.getByRole("radio", { name: "Same as the device" })).toBeChecked();
    await page.screenshot({ path: `test-results/a11y/profile-preferences-${scheme}.png` });
    await expectNoViolation(page);

    // Security (T2): change the password under the policy in words, then the security activity, newest first.
    await page.keyboard.press("ArrowRight");
    const security = page.getByRole("tabpanel", { name: "Security" });
    await expect(security.getByRole("list", { name: "Your password needs" })).toContainText("At least 12 characters.");
    await expect(security.getByRole("checkbox", { name: /End my other sessions/ })).toBeChecked();
    await expect(security.getByRole("table", { name: "Your security activity" })).toContainText(
      "Platform Operator, the platform operator",
    );
    await page.screenshot({ path: `test-results/a11y/profile-security-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.keyboard.press("ArrowRight");
    const sessions = page.getByRole("tabpanel", { name: "Sessions" });
    await expect(sessions).toContainText("Ends at the latest at");
    await expect(sessions.getByRole("table", { name: /Your live sessions/ })).toContainText("Chrome on Windows");
    await page.screenshot({ path: `test-results/a11y/profile-session-${scheme}.png` });
    await expectNoViolation(page);
  });

  test(`inviting a person in the ${scheme} scheme: the form, then the link shown once, with no WCAG 2.1 AA violation [TF-172 AC1, ADR-0033]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await stubAccess(page);
    await page.route("**/api/v1/users", (route) =>
      route.request().method() === "POST"
        ? route.fulfill({
            status: 201,
            json: {
              user: {
                ...record,
                id: "01a14e12-0000-7000-8000-0000000000aa",
                display_name: "Katherine Johnson",
                status: "invited",
                home_unit_id: record.home_unit.id,
              },
              invitation_link: "/invitations#a-token-shown-once",
              invitation_expires_at: "2026-10-22T10:00:00Z",
            },
          })
        : route.fallback(),
    );
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
    await page.goto("/users?invite=1");

    const dialog = page.getByRole("dialog", { name: "Invite a person" });
    await expect(dialog.getByRole("textbox", { name: "Display name" })).toBeFocused();
    await dialog.getByRole("textbox", { name: "Display name" }).fill("Katherine Johnson");
    await dialog.getByRole("textbox", { name: /^Login identifier/ }).fill("katherine@acme.localhost");
    await expect(dialog.getByRole("tree", { name: "Home unit" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/invite-${scheme}.png` });
    await expectNoViolation(page);

    await dialog.getByRole("button", { name: "Invite" }).click();
    const done = page.getByRole("dialog", { name: "Katherine Johnson is invited" });
    await expect(done.getByRole("textbox", { name: "Invitation link" })).toHaveValue(
      /\/invitations#a-token-shown-once$/,
    );
    await expect(done).toContainText("Email delivery comes later");
    await page.screenshot({ path: `test-results/a11y/invite-link-${scheme}.png` });
    await expectNoViolation(page);
  });
}
