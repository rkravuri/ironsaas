import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Route, Routes } from "react-router";
import type { KillSwitch } from "@/api/flags";
import { SessionProvider } from "@/auth/session";
import { formatDateTime } from "@/i18n/dates";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { cssVariablesResolver, theme } from "@/theme";
import { KillSwitchBanner } from "./KillSwitchBanner";
import { Layout } from "./Layout";

const administrator = ["authz.roles.manage", "authz.roles.read"];
const entitlements = {
  revision: 1,
  plan: "enterprise",
  confirmed_at: "2026-10-20T09:00:00Z",
  stale: false,
  modules: [],
  features: [],
};
const killed: KillSwitch = {
  path: "imports",
  flag: "kill.imports",
  since: "2026-10-20T08:30:00Z",
  reason: "An import fault is being fixed.",
};

/** What /me/context answers: the API puts the kill switches in for administrators only, so the stub decides who sees them. */
const context = (killSwitches: KillSwitch[]) => ({
  entitlements,
  flags: {
    known: true,
    confirmed_at: "2026-10-20T09:00:00Z",
    values: [{ key: "kill.imports", type: "boolean", value: killSwitches.length > 0, reason: "targeting_match" }],
    kill_switches: killSwitches,
  },
});

function renderShell() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MantineProvider theme={theme} cssVariablesResolver={cssVariablesResolver}>
      <QueryClientProvider client={queryClient}>
        <SessionProvider>
          <MemoryRouter initialEntries={["/"]}>
            <Routes>
              <Route path="/" element={<Layout />}>
                <Route index element={<h1>The page's work</h1>} />
              </Route>
            </Routes>
          </MemoryRouter>
        </SessionProvider>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("the kill-switch banner (TF-051, ADR-0035)", () => {
  it("tells an administrator which path is stopped, since when and why, in words with an icon, above the page [ADR-0035]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(administrator)),
      "GET /api/v1/me/context": () => json(context([killed])),
    });
    renderShell();

    const banner = await screen.findByRole("status");
    expect(banner).toHaveTextContent("The platform's operator has stopped imports for now");
    // In the person's zone, with the zone named (ADR-0050).
    expect(banner).toHaveTextContent(`Since ${formatDateTime(killed.since)}.`);
    expect(banner).toHaveTextContent("The operator's reason: An import fault is being fixed.");
    // The icon is beside the words, never instead of them, and has no name of its own.
    expect(banner.querySelector("svg")).toHaveAttribute("aria-hidden", "true");
    // In the page's flow, before the work: it pushes the page down and covers nothing.
    const work = screen.getByRole("heading", { name: "The page's work" });
    expect(banner.compareDocumentPosition(work) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(banner.closest("main")).toBe(work.closest("main"));
  });

  it("shows nothing when the answer has no kill switches, as it is for a person who is not an administrator [ADR-0035]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["identity.users.read"])),
      "GET /api/v1/me/context": () => json(context([])),
    });
    renderWithProviders(<KillSwitchBanner />);

    await waitFor(() => expect(calls.some((call) => call.key === "GET /api/v1/me/context")).toBe(true));
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("goes within 30 seconds of the operator turning the switch off, and says when no reason was given [ADR-0035]", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let answer = context([{ ...killed, reason: null }]);
    stubFetch({ "GET /api/v1/me": () => json(me(administrator)), "GET /api/v1/me/context": () => json(answer) });
    renderWithProviders(<KillSwitchBanner />);

    expect(await screen.findByRole("status")).toHaveTextContent("The operator gave no reason.");

    answer = context([]);
    await vi.advanceTimersByTimeAsync(30_000);

    await waitFor(() => expect(screen.queryByRole("status")).not.toBeInTheDocument());
  });
});
