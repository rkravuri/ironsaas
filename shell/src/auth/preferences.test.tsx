import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MantineProvider, useMantineColorScheme } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { SessionProvider } from "@/auth/session";
import { densityStorageKey, setDensity, useDensity } from "@/design/density";
import { colorSchemeManager, schemeStorageKey } from "@/design/scheme";
import { json, me, stubFetch } from "@/test/render";
import { cssVariablesResolver, theme } from "@/theme";
import { usePreferenceSync } from "./preferences";

const entitlements = {
  revision: 1,
  plan: "enterprise",
  confirmed_at: "2026-10-20T09:00:00Z",
  stale: false,
  modules: [],
  features: [],
};
type Level = "default" | "user";
const context = (scheme: [string, Level], density: [string, Level]) => ({
  entitlements,
  settings: {
    values: [
      { key: "shell.colour_scheme", value: scheme[0], level: scheme[1] },
      { key: "shell.density", value: density[0], level: density[1] },
    ],
  },
});

function Probe() {
  usePreferenceSync();
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const density = useDensity();
  return (
    <>
      <p data-testid="local">{`${colorScheme} ${density}`}</p>
      <button type="button" onClick={() => setColorScheme("light")}>
        Light
      </button>
    </>
  );
}

function renderSynced() {
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
          <Probe />
        </SessionProvider>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const saves = (calls: { key: string; init: RequestInit | undefined }[]) =>
  calls
    .filter((call) => call.key.startsWith("PUT /api/v1/settings/"))
    .map((call) => `${call.key.slice("PUT /api/v1/settings/".length)} ${call.init?.body as string}`);

/** The setting as the API answers a save. */
const saved = (key: string, value: string) => ({
  key,
  module: "settings",
  type: "choice",
  description: "A preference.",
  requirement: "One of the choices.",
  choices: [value],
  minimum: null,
  maximum: null,
  whole_number: false,
  scopes: ["user"],
  default: value,
  value,
  source: { level: "user", unit: null, changed_by: null, changed_at: null },
  set_here: true,
});

beforeEach(() => {
  window.localStorage.clear();
  setDensity("comfortable");
  window.localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("a person's preferences follow them to another browser (ADR-0036)", () => {
  it("applies the person's own values after sign-in and keeps them as this browser's copy for the next first paint", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/me/context": () => json(context(["dark", "user"], ["compact", "user"])),
    });
    renderSynced();

    await waitFor(() => expect(screen.getByTestId("local")).toHaveTextContent("dark compact"));
    expect(window.localStorage.getItem(schemeStorageKey)).toBe("dark");
    expect(window.localStorage.getItem(densityStorageKey)).toBe("compact");
    // Taken from the server, so nothing is sent back.
    expect(saves(calls)).toEqual([]);
  });

  it("saves a change made in this browser as the person's own value", async () => {
    let answer = context(["auto", "default"], ["comfortable", "default"]);
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/me/context": () => json(answer),
      "PUT /api/v1/settings/shell.colour_scheme": () => {
        answer = context(["light", "user"], ["comfortable", "default"]);
        return json(saved("shell.colour_scheme", "light"));
      },
    });
    renderSynced();
    await waitFor(() => expect(screen.getByTestId("local")).toHaveTextContent("auto comfortable"));
    expect(saves(calls)).toEqual([]);

    await userEvent.setup().click(screen.getByRole("button", { name: "Light" }));

    await waitFor(() =>
      expect(saves(calls)).toEqual(['shell.colour_scheme {"scope":"user","unit_id":null,"value":"light"}']),
    );
    await waitFor(() =>
      expect(calls.filter((call) => call.key === "GET /api/v1/me/context").length).toBeGreaterThan(1),
    );
    expect(screen.getByTestId("local")).toHaveTextContent("light comfortable");
  });

  it("takes up the choice this browser had when the person has no value yet", async () => {
    window.localStorage.setItem(schemeStorageKey, "dark");
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/me/context": () => json(context(["auto", "default"], ["comfortable", "default"])),
      "PUT /api/v1/settings/shell.colour_scheme": () => json(saved("shell.colour_scheme", "dark")),
    });
    renderSynced();

    await waitFor(() =>
      expect(saves(calls)).toEqual(['shell.colour_scheme {"scope":"user","unit_id":null,"value":"dark"}']),
    );
    expect(screen.getByTestId("local")).toHaveTextContent("dark comfortable");
  });
});
