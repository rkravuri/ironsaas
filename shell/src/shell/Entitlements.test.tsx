import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Route, Routes } from "react-router";
import { SessionProvider } from "@/auth/session";
import { useFeature } from "@/auth/entitlements";
import { json, me, stubFetch } from "@/test/render";
import { cssVariablesResolver, theme } from "@/theme";
import { RequirePermission } from "./Guards";
import { Layout } from "./Layout";

// A licensed module beside the real ones: no business or product module exists yet (ADR-0034), so a fixture stands in.
vi.mock("@/modules", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/modules")>();
  return {
    manifests: [
      ...original.manifests,
      {
        name: "sourcing",
        titleKey: "modules.console.title",
        licence: "sourcing",
        routes: [{ path: "/sourcing", permission: null, element: null, titleKey: "modules.console.title" }],
        navigation: [{ labelKey: "modules.console.units", path: "/sourcing", permission: null }],
      },
    ],
  };
});

const permissions = ["identity.users.read"];
const context = (sourcing: "enabled" | "disabled", transfer: "enabled" | "read_only" | "disabled") => ({
  entitlements: {
    revision: 3,
    plan: "enterprise",
    confirmed_at: "2026-10-20T09:00:00Z",
    stale: false,
    modules: [{ code: "sourcing", status: sourcing, reason: sourcing === "enabled" ? "licensed" : "not_licensed" }],
    features: [{ code: "authorization.role_transfer", status: transfer, reason: "licensed" }],
  },
});

function Probe() {
  const transfer = useFeature("authorization.role_transfer");
  return <p data-testid="transfer">{`${transfer.available ? "available" : "unavailable"} ${transfer.writable ? "writable" : "read only"}`}</p>;
}

function renderShell(route = "/") {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MantineProvider theme={theme} cssVariablesResolver={cssVariablesResolver}>
      <QueryClientProvider client={queryClient}>
        <SessionProvider>
          <MemoryRouter initialEntries={[route]}>
            <Routes>
              <Route path="/" element={<Layout />}>
                <Route index element={<Probe />} />
                <Route
                  path="sourcing"
                  element={
                    <RequirePermission permission={null} licence="sourcing">
                      <p>the sourcing page</p>
                    </RequirePermission>
                  }
                />
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

describe("entitlements in the shell (TF-043, ADR-0034)", () => {
  it("reads /me/context with the session and again within 30 seconds, and the navigation follows without a new sign-in", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let answer = context("enabled", "enabled");
    const { calls } = stubFetch({ "GET /api/v1/me": () => json(me(permissions)), "GET /api/v1/me/context": () => json(answer) });
    renderShell();

    const navigation = await screen.findByRole("navigation", { name: "Main navigation" });
    await waitFor(() => expect(within(navigation).getByRole("link", { name: "Organisation units" })).toBeInTheDocument());
    expect(screen.getByTestId("transfer")).toHaveTextContent("available writable");
    const reads = calls.filter((call) => call.key === "GET /api/v1/me/context").length;

    // The operator moves the tenant to a plan without the module and with the feature read only.
    answer = context("disabled", "read_only");
    await vi.advanceTimersByTimeAsync(30_000);

    await waitFor(() => expect(within(navigation).queryByRole("link", { name: "Organisation units" })).not.toBeInTheDocument());
    expect(screen.getByTestId("transfer")).toHaveTextContent("available read only");
    expect(calls.filter((call) => call.key === "GET /api/v1/me/context").length).toBeGreaterThan(reads);
  });

  it("answers a link to a module that is not entitled with the subscription's words, not the permission's", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me(permissions)), "GET /api/v1/me/context": () => json(context("disabled", "disabled")) });
    renderShell("/sourcing");

    expect(await screen.findByRole("heading", { name: "Not in your subscription" })).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent("Your organisation's subscription does not include this.");
    expect(screen.queryByText("the sourcing page")).not.toBeInTheDocument();
  });

  it("hides what is licensed while /me/context does not answer, and shows it once it does", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me(permissions)) });
    renderShell();

    await screen.findByRole("navigation", { name: "Main navigation" });
    await waitFor(() => expect(screen.getByTestId("transfer")).toHaveTextContent("unavailable read only"));
  });
});
