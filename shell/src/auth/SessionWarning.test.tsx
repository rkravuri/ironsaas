import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Route, Routes } from "react-router";
import { LoginPage } from "./LoginPage";
import { SessionProvider } from "./session";
import { SessionWarning } from "./SessionWarning";
import { RedirectIfSignedIn, RequireSession } from "@/shell/Guards";
import { fill, json, me, problem, sessionTimes, stubFetch } from "@/test/render";
import { cssVariablesResolver, theme } from "@/theme";

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

/**
 * One browser tab: its own query cache and session provider, the users page behind the session guard with the warning,
 * and the login page. Two of these in one test are two tabs: their providers talk over the real BroadcastChannel.
 */
function openTab(route = "/users") {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MantineProvider theme={theme} cssVariablesResolver={cssVariablesResolver}>
      <QueryClientProvider client={queryClient}>
        <SessionProvider>
          <MemoryRouter initialEntries={[route]}>
            <Routes>
              <Route
                path="/login"
                element={
                  <RedirectIfSignedIn>
                    <LoginPage autoFocus={false} />
                  </RedirectIfSignedIn>
                }
              />
              <Route
                path="/users"
                element={
                  <RequireSession>
                    <button type="button">Where I was</button>
                    <p>the users page</p>
                    <SessionWarning />
                  </RequireSession>
                }
              />
            </Routes>
          </MemoryRouter>
        </SessionProvider>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe("the session warning", () => {
  it("takes focus two minutes before the idle end, and one click on Stay signed in extends the session", async () => {
    let session = sessionTimes({ idleIn: 90_000 });
    const { calls } = stubFetch({ "GET /api/v1/me": () => json({ ...me(["identity.users.read"]), session }) });
    openTab();

    const dialog = await screen.findByRole("alertdialog", { name: "Your session is about to end" });
    expect(dialog).toHaveAccessibleDescription(/ends at .+ Stay signed in to carry on/);
    const stay = screen.getByRole("button", { name: "Stay signed in" });
    await waitFor(() => expect(stay).toHaveFocus());

    session = sessionTimes();
    const before = calls.filter((call) => call.key === "GET /api/v1/me").length;
    await userEvent.setup().click(stay);

    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    expect(calls.filter((call) => call.key === "GET /api/v1/me")).toHaveLength(before + 1);
    expect(screen.getByText("the users page")).toBeInTheDocument();
  });

  it("closes in every tab when one tab extends the session", async () => {
    let session = sessionTimes({ idleIn: 90_000 });
    stubFetch({ "GET /api/v1/me": () => json({ ...me(["identity.users.read"]), session }) });
    openTab();
    openTab();
    await waitFor(() => expect(screen.getAllByRole("alertdialog")).toHaveLength(2));

    session = sessionTimes();
    await userEvent.setup().click(screen.getAllByRole("button", { name: "Stay signed in" })[0]!);

    // Only the first tab asked the API; the second heard of the new idle end over the channel.
    await waitFor(() => expect(screen.queryAllByRole("alertdialog")).toHaveLength(0));
  });

  it("says plainly near the absolute limit that the session cannot be extended", async () => {
    stubFetch({ "GET /api/v1/me": () => json({ ...me(["identity.users.read"]), session: sessionTimes({ endsIn: 100_000 }) }) });
    openTab();

    const dialog = await screen.findByRole("alertdialog", { name: "This session cannot be extended" });
    expect(dialog).toHaveAccessibleDescription(/cannot be extended any further: save your work, then sign in again/);
    expect(screen.queryByRole("button", { name: "Stay signed in" })).not.toBeInTheDocument();

    await userEvent.setup().click(screen.getByRole("button", { name: "Back to my work" }));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
  });

  it("signs out when the idle end passes, says why on the login page, and returns to the same page after signing in", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let signedIn = true;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => (signedIn ? json({ ...me(["identity.users.read"]), session: sessionTimes({ idleIn: 1_000 }) }) : problem(401, { error_code: "auth.not_authenticated" })),
      "POST /auth/logout": () => {
        signedIn = false;
        return new Response(null, { status: 204 });
      },
      "POST /auth/login": () => {
        signedIn = true;
        return json({ principal: {}, session: {} });
      },
    });
    openTab();
    await screen.findByText("the users page");
    await vi.advanceTimersByTimeAsync(1_500);

    const reason = await screen.findByRole("status");
    expect(reason).toHaveTextContent("You were signed out because nothing happened for a while.");
    expect(calls.some((call) => call.key === "POST /auth/logout")).toBe(true);

    const user = userEvent.setup();
    await fill(user, screen.getByLabelText(/login identifier/i), "admin@acme.localhost");
    await fill(user, screen.getByLabelText(/^password/i), "correct horse battery staple");
    // Signed in again, the next /me must not end at once: the far idle end of a fresh session.
    stubFetch({
      "GET /api/v1/me": () => json(me(["identity.users.read"])),
      "POST /auth/login": () => json({ principal: {}, session: {} }),
    });
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    expect(await screen.findByText("the users page")).toBeInTheDocument();
  });

  it("takes every tab to the login page when one tab signs out, and each says why", async () => {
    let signedIn = true;
    stubFetch({
      "GET /api/v1/me": () => (signedIn ? json({ ...me(["identity.users.read"]), session: sessionTimes({ idleIn: 90_000 }) }) : problem(401, { error_code: "auth.not_authenticated" })),
      "POST /auth/logout": () => {
        signedIn = false;
        return new Response(null, { status: 204 });
      },
    });
    openTab();
    openTab();
    await waitFor(() => expect(screen.getAllByRole("alertdialog")).toHaveLength(2));

    await userEvent.setup().click(screen.getAllByRole("button", { name: "Sign out" })[0]!);

    await waitFor(() => expect(screen.getAllByRole("status")).toHaveLength(2));
    const sentences = screen.getAllByRole("status").map((status) => status.textContent);
    expect(sentences).toContain("You have signed out.");
    expect(sentences).toContain("You signed out in another tab.");
    expect(screen.queryAllByRole("alertdialog")).toHaveLength(0);
  });
});
