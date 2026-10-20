import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import { dayPart } from "./greeting";
import { HomePage } from "./HomePage";
import { readRecent, recentStorageKey, rememberPage } from "./recent";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";

const root = { id: "01a15100-0000-7000-8000-000000000001", name: "Acme Corporation", code: "acme" };
const branch = { id: "01a15100-0000-7000-8000-000000000009", name: "E2E branch", code: "e2e-branch" };
const administrator = ["authz.assignments.manage", "authz.assignments.read", "authz.roles.manage", "authz.roles.read", "authz.units.manage", "authz.units.read", "identity.users.manage", "identity.users.read"];
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
  it("greets the person by the time of day, with the Home drawing, and opens every module a tenant administrator may open", async () => {
    signIn({ anywhere: administrator, atHome: administrator, unit: root });
    renderWithProviders(<HomePage />);

    expect(await screen.findByRole("heading", { level: 1, name: /^Good (morning|afternoon|evening), Admin$/ })).toBeInTheDocument();
    expect(screen.getByRole("img", { name: /A small building with its flag up/ })).toBeInTheDocument();
    expect(screen.getByText(/Your home unit is Acme Corporation/)).toBeInTheDocument();

    expect(within(opener("Identity")).getByRole("link", { name: "Users" })).toHaveAttribute("href", "/users");
    expect(within(opener("Identity")).getByText(/people and service accounts/)).toBeInTheDocument();
    expect(within(opener("Console")).getByRole("link", { name: "Organisation units" })).toHaveAttribute("href", "/console/units");
    expect(within(opener("Console")).getByRole("link", { name: "Roles" })).toBeInTheDocument();

    const actions = await screen.findByRole("region", { name: "Quick actions" });
    expect(within(actions).getByRole("link", { name: "Invite a person" })).toHaveAttribute("href", "/users?invite=1");
    expect(within(actions).getByRole("link", { name: "Add a unit" })).toHaveAttribute("href", "/console/units?add=1");
    expect(screen.getByRole("region", { name: "Recently opened" })).toHaveTextContent(/The last five pages you open in this browser will appear here/);
  });

  it("shows a child-unit administrator only the openers it may open, and no quick action it cannot perform", async () => {
    const { calls } = signIn({ anywhere: ["identity.users.read"], atHome: ["identity.users.read"], unit: branch });
    renderWithProviders(<HomePage />);

    expect(await screen.findByRole("article", { name: "Identity" })).toBeInTheDocument();
    // The console's one page for everyone is Delegations (ADR-0037); its administration pages stay away.
    const opener = screen.getByRole("article", { name: "Console" });
    expect(within(opener).getAllByRole("link").map((link) => link.textContent)).toEqual(["Delegations"]);
    expect(screen.queryByRole("region", { name: "Quick actions" })).not.toBeInTheDocument();
    // Nothing to ask: no quick action's permission is held anywhere.
    expect(calls.some((call) => call.key.startsWith("GET /api/v1/me/access"))).toBe(false);
  });

  it("asks /me/access at the home unit: a permission held elsewhere does not make a quick action here", async () => {
    signIn({ anywhere: ["identity.users.read", "identity.users.manage"], atHome: ["identity.users.read"], unit: branch });
    renderWithProviders(<HomePage />);

    await screen.findByRole("article", { name: "Identity" });
    await waitFor(() => expect(screen.queryByRole("region", { name: "Quick actions" })).not.toBeInTheDocument());
  });

  it("lists the pages opened last in this browser, newest first, leaving out those the person may no longer open", async () => {
    rememberPage(principal, { path: "/console/roles", titleKey: "modules.console.roles", module: "console", at: 1 });
    rememberPage(principal, { path: "/users", titleKey: "modules.identity.users", module: "identity", at: 2 });
    rememberPage(principal, { path: "/users/42", titleKey: "modules.identity.users", module: "identity", at: 3 });
    window.localStorage.setItem(recentStorageKey(principal), JSON.stringify(readRecent(principal).map((page) => (page.path === "/users/42" ? { ...page, label: "Ada Lovelace" } : page))));
    signIn({ anywhere: ["identity.users.read"], atHome: ["identity.users.read"], unit: branch });
    renderWithProviders(<HomePage />);

    const recent = await screen.findByRole("region", { name: "Recently opened" });
    const links = within(recent).getAllByRole("link");
    expect(links.map((link) => link.textContent)).toEqual(["Ada Lovelace", "Users"]);
    expect(links[0]).toHaveAttribute("href", "/users/42");
    expect(recent).toHaveTextContent("Users, Identity");
  });

  it("draws the marks in once per session", async () => {
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
  it("names the part of the day", () => {
    expect([4, 5, 11, 12, 17, 18, 23].map(dayPart)).toEqual(["evening", "morning", "morning", "afternoon", "afternoon", "evening", "evening"]);
  });
});

describe("the recent pages", () => {
  it("keeps five, newest first, once each, per person, and survives a damaged store", () => {
    for (let n = 1; n <= 7; n += 1) {
      rememberPage(principal, { path: `/users/${n}`, titleKey: "modules.identity.users", module: "identity", at: n });
    }
    rememberPage(principal, { path: "/users/5", titleKey: "modules.identity.users", module: "identity", at: 8 });
    expect(readRecent(principal).map((page) => page.path)).toEqual(["/users/5", "/users/7", "/users/6", "/users/4", "/users/3"]);
    expect(readRecent("someone-else")).toEqual([]);

    window.localStorage.setItem(recentStorageKey(principal), "not json");
    expect(readRecent(principal)).toEqual([]);
  });
});
