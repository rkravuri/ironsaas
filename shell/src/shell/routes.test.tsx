import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within, render } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createMemoryRouter, RouterProvider } from "react-router";
import { ApiError } from "@/api/client";
import { SessionProvider } from "@/auth/session";
import { colorSchemeManager } from "@/design/scheme";
import type { ModuleManifest } from "@/modules/registry";
import { json, me, stubFetch } from "@/test/render";
import { cssVariablesResolver, theme } from "@/theme";
import en from "@/i18n/en.json";
import { shellRoutes } from "./routes";

const home = { id: "01a15100-0000-7000-8000-000000000001", name: "Acme Corporation", code: "acme" };
const permissions = ["identity.users.read"];

/** A page that fails while it renders, as an unhandled error does, carrying the API's correlation id. */
function Broken(): never {
  throw new ApiError(500, { title: "Internal Server Error", error_code: "api.unexpected" }, "corr-broken");
}

const probe: ModuleManifest = {
  name: "probe",
  titleKey: "modules.identity.title",
  routes: [
    { path: "/broken", titleKey: "modules.identity.title", permission: "identity.users.read", element: <Broken /> },
  ],
  navigation: [],
};

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("the shell's routes", () => {
  it("show an unhandled error of a page with its correlation id inside the shell, which stays usable [TF-170 AC2, ADR-0020]", async () => {
    // The router logs what it caught, as it does for whoever is looking; the test keeps the console quiet.
    vi.spyOn(console, "error").mockImplementation(() => {});
    stubFetch({
      "GET /api/v1/me": () => json({ ...me(permissions), home_unit: home }),
      [`GET /api/v1/me/access?unit=${home.id}`]: () => json({ scope: "unit", permissions }),
    });
    const router = createMemoryRouter(shellRoutes([probe], { strict: false }), { initialEntries: ["/broken"] });
    render(
      <MantineProvider
        theme={theme}
        colorSchemeManager={colorSchemeManager}
        cssVariablesResolver={cssVariablesResolver}
      >
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <SessionProvider>
            <RouterProvider router={router} />
          </SessionProvider>
        </QueryClientProvider>
      </MantineProvider>,
    );

    // The friendly page, with the id to quote to support.
    expect(await screen.findByRole("heading", { name: en.errors.title })).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent("corr-broken");

    // The shell is still there around it, and its navigation still works.
    const navigation = screen.getByRole("navigation", { name: en.app.navigation });
    await userEvent.setup().click(within(navigation).getByRole("link", { name: en.home.title }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(screen.queryByText("corr-broken")).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: en.errors.title })).not.toBeInTheDocument();
  });
});
