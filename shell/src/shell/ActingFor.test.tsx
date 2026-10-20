import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Route, Routes } from "react-router";
import { SessionProvider } from "@/auth/session";
import { colorSchemeManager } from "@/design/scheme";
import { json, me, problem, stubFetch, type FetchHandler } from "@/test/render";
import { cssVariablesResolver, theme } from "@/theme";
import { Layout } from "./Layout";

const ada = "01a17a00-0000-7000-8000-0000000000a1";
const userAdministration = { code: "identity.user_administration", name: "user administration" };
const until = "2026-10-27T00:00:00Z";
const received = { delegator_id: ada, delegator_name: "Ada Lovelace", delegate_id: me().id, delegate_name: "Admin", duties: [userAdministration], until };
const actingFor = { id: ada, name: "Ada Lovelace", duties: [userAdministration], until };

/** /me with the delegations a person gives and holds, and whom the session acts for. */
const meWith = ({ holds = false, acting = false }: { holds?: boolean; acting?: boolean }) => ({
  ...me(["identity.users.read"]),
  acting_for: acting ? actingFor : null,
  delegations: { given: [], received: holds ? [received] : [] },
});

function renderShell(handlers: Record<string, FetchHandler>) {
  const stub = stubFetch(handlers);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={theme} colorSchemeManager={colorSchemeManager} cssVariablesResolver={cssVariablesResolver}>
      <QueryClientProvider client={queryClient}>
        <SessionProvider>
          <MemoryRouter initialEntries={["/"]}>
            <Routes>
              <Route path="/" element={<Layout />}>
                <Route index element={<p>home</p>} />
              </Route>
            </Routes>
          </MemoryRouter>
        </SessionProvider>
      </QueryClientProvider>
    </MantineProvider>,
  );
  return stub;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the Acting for control (TF-037, ADR-0033, ADR-0037)", () => {
  it("is not there for a person who holds no active delegation", async () => {
    renderShell({ "GET /api/v1/me": () => json(meWith({})) });

    expect(await screen.findByRole("button", { name: /signed in as admin/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /act for/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/you are acting for/i)).not.toBeInTheDocument();
  });

  it("appears with a delegation, acts for the delegator, shows the band, and stops", async () => {
    const { calls } = renderShell({
      "GET /api/v1/me": () => json(meWith({ holds: true })),
      "POST /api/v1/me/acting-for": () => json(meWith({ holds: true, acting: true })),
      "DELETE /api/v1/me/acting-for": () => json(meWith({ holds: true })),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Act for someone who delegated duties to you" }));
    await user.click(await screen.findByRole("menuitem", { name: /Ada Lovelace/ }));

    expect(await screen.findByText("You are acting for Ada Lovelace")).toBeInTheDocument();
    expect(screen.getByText(/You use Ada Lovelace's rights for user administration until/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Acting for Ada Lovelace/ })).toBeInTheDocument();
    const posted = calls.find((call) => call.key === "POST /api/v1/me/acting-for");
    expect(JSON.parse(posted?.init?.body as string)).toEqual({ principal_id: ada });

    await user.click(screen.getByRole("button", { name: "Act as myself" }));

    await waitFor(() => expect(screen.queryByText("You are acting for Ada Lovelace")).not.toBeInTheDocument());
    expect(calls.some((call) => call.key === "DELETE /api/v1/me/acting-for")).toBe(true);
    expect(screen.getByRole("button", { name: "Act for someone who delegated duties to you" })).toBeInTheDocument();
  });

  it("says so when the delegation ended while acting, and the person acts as themselves again", async () => {
    let mine = 0;
    let context = 0;
    renderShell({
      // Acting at first; after the API said the delegation ended, as the person themselves with nothing held.
      "GET /api/v1/me": () => json(mine++ === 0 ? meWith({ holds: true, acting: true }) : meWith({})),
      "GET /api/v1/me/context": () => {
        const answer = problem(404, { title: "Not Found", error_code: "test.unhandled" });
        if (context++ === 0) {
          answer.headers.set("Iron-Acting-Ended", ada);
        }
        return answer;
      },
    });

    expect(await screen.findByText("You are acting for Ada Lovelace")).toBeInTheDocument();

    expect(await screen.findByText("The delegation from Ada Lovelace has ended, so you act as yourself again.")).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText("You are acting for Ada Lovelace")).not.toBeInTheDocument());
    expect(screen.queryByRole("button", { name: /act for/i })).not.toBeInTheDocument();
  });
});
