import { runInNewContext } from "node:vm";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
// The script as text: the browser runs it as a classic script before React, never as a module.
import schemeScript from "../../public/scheme.js?raw";
import { SessionProvider } from "@/auth/session";
import { colorSchemeManager, schemeStorageKey } from "@/design/scheme";
import { fill, json, me, stubFetch } from "@/test/render";
import { cssVariablesResolver, theme } from "@/theme";
import { Layout } from "./Layout";

const administrator = [
  "authz.roles.read",
  "authz.units.manage",
  "authz.units.read",
  "identity.users.manage",
  "identity.users.read",
];
const home = { id: "01a15100-0000-7000-8000-000000000001", name: "Acme Corporation", code: "acme" };

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

/** The shell's frame around placeholder pages, with the API answered from memory. */
function renderShell({ permissions = administrator, route = "/" }: { permissions?: string[]; route?: string } = {}) {
  stubFetch({
    "GET /api/v1/me": () => json({ ...me(permissions), home_unit: home }),
    [`GET /api/v1/me/access?unit=${home.id}`]: () => json({ scope: "unit", permissions }),
  });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MantineProvider
      theme={theme}
      defaultColorScheme="auto"
      colorSchemeManager={colorSchemeManager}
      cssVariablesResolver={cssVariablesResolver}
    >
      <QueryClientProvider client={queryClient}>
        <SessionProvider>
          <MemoryRouter initialEntries={[route]}>
            <Routes>
              <Route path="/" element={<Layout />}>
                <Route index element={<Where />} />
                <Route path="*" element={<Where />} />
              </Route>
            </Routes>
          </MemoryRouter>
        </SessionProvider>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

beforeEach(() => {
  window.localStorage.clear();
  document.documentElement.removeAttribute("data-mantine-color-scheme");
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the header", () => {
  it("opens the search with Ctrl+K: every page the principal may open and the quick actions; Enter opens the first match [ADR-0033]", async () => {
    renderShell();
    const user = userEvent.setup();
    await screen.findByRole("button", { name: /signed in as admin/i });

    await user.keyboard("{Control>}k{/Control}");
    const search = await screen.findByRole("combobox", { name: "Search pages and actions" });
    const palette = within(search.closest("[role=dialog]") as HTMLElement);
    for (const name of ["Home", "Users", "My profile", "Organisation units", "Roles"]) {
      expect(palette.getByRole("option", { name: new RegExp(`^${name}`) })).toBeInTheDocument();
    }
    // The quick actions come once /me/access has answered for the home unit.
    expect(await palette.findByRole("option", { name: /^Invite a person/ })).toBeInTheDocument();
    expect(palette.getByRole("option", { name: /^Add a unit/ })).toBeInTheDocument();

    await fill(user, search, "units");
    await user.keyboard("{Enter}");

    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/console/units"));
    await waitFor(() =>
      expect(screen.queryByRole("combobox", { name: "Search pages and actions" })).not.toBeInTheDocument(),
    );
  });

  it("reaches only what the principal may open: a reader of users finds no console page and no quick action [ADR-0033]", async () => {
    renderShell({ permissions: ["identity.users.read"] });
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: /^Search/ }));

    const search = await screen.findByRole("combobox", { name: "Search pages and actions" });
    const palette = within(search.closest("[role=dialog]") as HTMLElement);
    expect(palette.getByRole("option", { name: /^Users/ })).toBeInTheDocument();
    expect(palette.queryByRole("option", { name: /^Organisation units/ })).not.toBeInTheDocument();
    expect(palette.queryByRole("option", { name: /^Roles/ })).not.toBeInTheDocument();
    expect(palette.queryByRole("option", { name: /^Invite a person/ })).not.toBeInTheDocument();
    expect(palette.queryByText("Quick actions")).not.toBeInTheDocument();
  });

  it("tells a screen reader which action Enter will open: the input points at the chosen option as the arrows move [ADR-0024, ADR-0033]", async () => {
    renderShell();
    const user = userEvent.setup();
    await screen.findByRole("button", { name: /signed in as admin/i });

    await user.keyboard("{Control>}k{/Control}");
    const search = await screen.findByRole("combobox", { name: "Search pages and actions" });
    const palette = within(screen.getByRole("listbox", { name: "Pages and actions that match" }));
    expect(search).not.toHaveAttribute("aria-activedescendant");

    // The keys go to the input itself, as Spotlight's own handler reads them there.
    fireEvent.keyDown(search, { key: "ArrowDown", code: "ArrowDown" });
    const first = palette.getByRole("option", { name: /^Home/ });
    await waitFor(() => expect(search).toHaveAttribute("aria-activedescendant", first.id));
    expect(first).toHaveAttribute("aria-selected", "true");

    fireEvent.keyDown(search, { key: "ArrowDown", code: "ArrowDown" });
    await waitFor(() => expect(search.getAttribute("aria-activedescendant")).not.toBe(first.id));
    const second = document.getElementById(search.getAttribute("aria-activedescendant") ?? "");
    expect(second).toHaveAttribute("role", "option");
    expect(second).toHaveAttribute("aria-selected", "true");
    expect(first).toHaveAttribute("aria-selected", "false");
    expect(palette.getByRole("group", { name: "Pages" })).toContainElement(second);
  });

  it("closes the search with Escape and gives focus back where it was [ADR-0033]", async () => {
    renderShell();
    const user = userEvent.setup();
    const help = await screen.findByRole("button", { name: "Help and keyboard shortcuts" });
    help.focus();

    await user.keyboard("{Control>}k{/Control}");
    await screen.findByRole("combobox", { name: "Search pages and actions" });
    await user.keyboard("{Escape}");

    await waitFor(() =>
      expect(screen.queryByRole("combobox", { name: "Search pages and actions" })).not.toBeInTheDocument(),
    );
    await waitFor(() => expect(help).toHaveFocus());
  });

  it("opens help with the shell's keys and the current page's keys [ADR-0033]", async () => {
    renderShell({ route: "/users" });
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Help and keyboard shortcuts" }));

    const panel = await screen.findByRole("dialog", { name: "Help" });
    expect(within(panel).getByRole("table", { name: "Keys on every page" })).toBeInTheDocument();
    expect(within(panel).getByRole("table", { name: "Keys on Users" })).toHaveTextContent("Open the selected row");
    expect(panel).toHaveTextContent(/The guide to each page will open from here/);
  });

  it("puts Home first in the navigation and shows no control without a function [ADR-0033]", async () => {
    renderShell();
    const navigation = await screen.findByRole("navigation", { name: "Main navigation" });
    const links = within(navigation).getAllByRole("link");
    expect(links[0]).toHaveAccessibleName("Home");
    expect(screen.queryByRole("button", { name: /notification/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /organisation|switch/i })).not.toBeInTheDocument();
  });
});

describe("the colour scheme", () => {
  it("is chosen in the profile menu, which shows the home unit, and survives a reload with the attribute set before React mounts [ADR-0025, ADR-0033]", async () => {
    const first = renderShell();
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: /signed in as admin/i }));
    const menu = await screen.findByRole("menu", { hidden: true });
    expect(menu).toHaveTextContent("Home unit: Acme Corporation");
    // jsdom lays nothing out, so Mantine hides a dropdown whose target has no size: the items are queried hidden or not.
    const item = (name: RegExp) => within(menu).getByRole("menuitem", { name, hidden: true });
    expect(item(/Same as the device, chosen/)).toBeInTheDocument();

    await user.click(item(/^Dark/));
    await waitFor(() => expect(document.documentElement).toHaveAttribute("data-mantine-color-scheme", "dark"));
    expect(window.localStorage.getItem(schemeStorageKey)).toBe("dark");
    expect(item(/Dark, chosen/)).toBeInTheDocument();
    first.unmount();

    // The reload: a fresh document on a light device, where scheme.js runs before anything else.
    document.documentElement.removeAttribute("data-mantine-color-scheme");
    runInNewContext(schemeScript, {
      window: { localStorage: window.localStorage, matchMedia: () => ({ matches: false }) },
      document,
    });
    expect(document.documentElement).toHaveAttribute("data-mantine-color-scheme", "dark");

    // React mounts on a root that is already dark, and keeps it.
    renderShell();
    expect(document.documentElement).toHaveAttribute("data-mantine-color-scheme", "dark");
    await screen.findByRole("button", { name: /signed in as admin/i });
    expect(document.documentElement).toHaveAttribute("data-mantine-color-scheme", "dark");
  });
});
