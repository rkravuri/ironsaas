import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import { dayPart } from "./greeting";
import { HomePage } from "./HomePage";
import { readRecent, recentStorageKey, rememberPage } from "./recent";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";

const root = { id: "01a15100-0000-7000-8000-000000000001", name: "Acme Corporation", code: "acme" };
const branch = { id: "01a15100-0000-7000-8000-000000000009", name: "E2E branch", code: "e2e-branch" };
const administrator = [
  "authz.assignments.manage",
  "authz.assignments.read",
  "authz.roles.manage",
  "authz.roles.read",
  "authz.units.manage",
  "authz.units.read",
  "identity.users.manage",
  "identity.users.read",
];
const principal = me().id;

function signIn({ anywhere, atHome, unit }: { anywhere: string[]; atHome: string[]; unit: typeof root }) {
  return stubFetch({
    "GET /api/v1/me": () => json({ ...me(anywhere), home_unit: unit }),
    [`GET /api/v1/me/access?unit=${unit.id}`]: () => json({ scope: "unit", permissions: atHome }),
  });
}

const opener = (name: string) => screen.getByRole("article", { name });

beforeEach(() => {
  window.localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the home page", () => {
  it("greets the person by the time of day, with the Home drawing, and opens every module a tenant administrator may open [ADR-0033, ADR-0043]", async () => {
    signIn({ anywhere: administrator, atHome: administrator, unit: root });
    renderWithProviders(<HomePage />);

    expect(
      await screen.findByRole("heading", { level: 1, name: /^Good (morning|afternoon|evening), Admin$/ }),
    ).toBeInTheDocument();
    expect(screen.getByRole("img", { name: /A small building with its flag up/ })).toBeInTheDocument();
    expect(screen.getByText(/Your home unit is Acme Corporation/)).toBeInTheDocument();

    // The kernel's modules are the Foundation's, grouped under its name before any suite's (ADR-0043).
    const foundation = screen.getByRole("region", { name: "IronSaaS Foundation" });
    expect(within(foundation).getByRole("article", { name: "Identity" })).toBeInTheDocument();
    expect(within(foundation).getByRole("article", { name: "Console" })).toBeInTheDocument();
    expect(screen.getAllByRole("heading", { level: 3 }).map((heading) => heading.textContent)).toEqual([
      "IronSaaS Foundation",
    ]);
    expect(within(opener("Identity")).getByRole("link", { name: "Users" })).toHaveAttribute("href", "/users");
    expect(within(opener("Identity")).getByText(/people and service accounts/)).toBeInTheDocument();
    expect(within(opener("Console")).getByRole("link", { name: "Organisation units" })).toHaveAttribute(
      "href",
      "/console/units",
    );
    expect(within(opener("Console")).getByRole("link", { name: "Roles" })).toBeInTheDocument();

    const actions = await screen.findByRole("region", { name: "Quick actions" });
    expect(within(actions).getByRole("link", { name: "Invite a person" })).toHaveAttribute("href", "/users?invite=1");
    expect(within(actions).getByRole("link", { name: "Add a unit" })).toHaveAttribute("href", "/console/units?add=1");
    expect(screen.getByRole("region", { name: "Recently opened" })).toHaveTextContent(
      /The last five pages you open in this browser will appear here/,
    );
  });

  it("shows the suite's group after the Foundation's where the unit runs the suite and the person is entitled to its module [TF-244 AC1, ADR-0059]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json({ ...me([...administrator, "purchasing.suite.read"]), home_unit: root }),
      [`GET /api/v1/me/access?unit=${root.id}`]: () => json({ scope: "unit", permissions: administrator }),
      "GET /api/v1/me/context": () =>
        json({
          entitlements: {
            revision: 3,
            plan: "standard",
            confirmed_at: "2026-10-30T09:00:00Z",
            stale: false,
            modules: [{ code: "purchasing", status: "enabled", reason: "licensed" }],
            features: [],
            suites: [{ code: "procurement", name: "eProcurement Suite", plan: "procurement-essentials" }],
          },
        }),
    });
    renderWithProviders(<HomePage />);

    const suite = await screen.findByRole("region", { name: "eProcurement Suite" });
    const purchasing = within(suite).getByRole("article", { name: "Purchasing" });
    expect(within(purchasing).getByRole("link", { name: "About this suite" })).toHaveAttribute(
      "href",
      "/procurement/about",
    );
    expect(screen.getAllByRole("heading", { level: 3 }).map((heading) => heading.textContent)).toEqual([
      "IronSaaS Foundation",
      "eProcurement Suite",
    ]);
  });

  it("shows a child-unit administrator only the openers it may open, and no quick action it cannot perform [ADR-0033]", async () => {
    const { calls } = signIn({ anywhere: ["identity.users.read"], atHome: ["identity.users.read"], unit: branch });
    renderWithProviders(<HomePage />);

    expect(await screen.findByRole("article", { name: "Identity" })).toBeInTheDocument();
    expect(screen.queryByRole("article", { name: "Console" })).not.toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Quick actions" })).not.toBeInTheDocument();
    // Nothing to ask: no quick action's permission is held anywhere.
    expect(calls.some((call) => call.key.startsWith("GET /api/v1/me/access"))).toBe(false);
  });

  it("asks /me/access at the home unit: a permission held elsewhere does not make a quick action here [ADR-0033]", async () => {
    signIn({
      anywhere: ["identity.users.read", "identity.users.manage"],
      atHome: ["identity.users.read"],
      unit: branch,
    });
    renderWithProviders(<HomePage />);

    await screen.findByRole("article", { name: "Identity" });
    await waitFor(() => expect(screen.queryByRole("region", { name: "Quick actions" })).not.toBeInTheDocument());
  });

  it("lists the pages opened last in this browser, newest first, leaving out those the person may no longer open [ADR-0033]", async () => {
    rememberPage(principal, { path: "/console/roles", titleKey: "modules.console.roles", module: "console", at: 1 });
    rememberPage(principal, { path: "/users", titleKey: "modules.identity.users", module: "identity", at: 2 });
    rememberPage(principal, { path: "/users/42", titleKey: "modules.identity.users", module: "identity", at: 3 });
    window.localStorage.setItem(
      recentStorageKey(principal),
      JSON.stringify(
        readRecent(principal).map((page) => (page.path === "/users/42" ? { ...page, label: "Ada Lovelace" } : page)),
      ),
    );
    signIn({ anywhere: ["identity.users.read"], atHome: ["identity.users.read"], unit: branch });
    renderWithProviders(<HomePage />);

    const recent = await screen.findByRole("region", { name: "Recently opened" });
    const links = within(recent).getAllByRole("link");
    expect(links.map((link) => link.textContent)).toEqual(["Ada Lovelace", "Users"]);
    expect(links[0]).toHaveAttribute("href", "/users/42");
    expect(recent).toHaveTextContent("Users, Identity");
  });

  it("draws the marks in once per session [ADR-0033]", async () => {
    signIn({ anywhere: administrator, atHome: administrator, unit: root });
    const first = renderWithProviders(<HomePage />);
    await screen.findByRole("article", { name: "Identity" });
    expect(document.querySelectorAll("[data-opener] .iron-draw-in").length).toBeGreaterThan(0);
    first.unmount();

    renderWithProviders(<HomePage />);
    await screen.findByRole("article", { name: "Identity" });
    expect(document.querySelectorAll("[data-opener] .iron-draw-in")).toHaveLength(0);
  });
});

describe("the greeting", () => {
  it("names the part of the day [ADR-0033]", () => {
    expect([4, 5, 11, 12, 17, 18, 23].map(dayPart)).toEqual([
      "evening",
      "morning",
      "morning",
      "afternoon",
      "afternoon",
      "evening",
      "evening",
    ]);
  });
});

describe("the recent pages", () => {
  it("keeps five, newest first, once each, per person, and survives a damaged store [ADR-0033]", () => {
    for (let n = 1; n <= 7; n += 1) {
      rememberPage(principal, { path: `/users/${n}`, titleKey: "modules.identity.users", module: "identity", at: n });
    }
    rememberPage(principal, { path: "/users/5", titleKey: "modules.identity.users", module: "identity", at: 8 });
    expect(readRecent(principal).map((page) => page.path)).toEqual([
      "/users/5",
      "/users/7",
      "/users/6",
      "/users/4",
      "/users/3",
    ]);
    expect(readRecent("someone-else")).toEqual([]);

    window.localStorage.setItem(recentStorageKey(principal), "not json");
    expect(readRecent(principal)).toEqual([]);
  });
});
