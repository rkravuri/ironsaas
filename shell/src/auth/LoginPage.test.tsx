import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { LoginPage } from "./LoginPage";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the login page", () => {
  it("signs in, re-reads /me and moves on", async () => {
    let signedIn = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => (signedIn ? json(me(["identity.users.read"])) : problem(401, { title: "Unauthorized", error_code: "auth.unauthenticated" })),
      "POST /auth/login": () => {
        signedIn = true;
        return json({ principal: {}, session: {} });
      },
    });
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<p>home</p>} />
      </Routes>,
      { route: "/login" },
    );

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText(/login identifier/i), "admin@acme.localhost");
    await user.type(screen.getByLabelText(/^password/i), "correct horse battery staple");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    await screen.findByText("home");
    expect(calls.some((c) => c.key === "POST /auth/login")).toBe(true);
  });

  it("shows the fixed message and the correlation id when the login is refused", async () => {
    stubFetch({
      "GET /api/v1/me": () => problem(401, { title: "Unauthorized", error_code: "auth.unauthenticated" }),
      "POST /auth/login": () => problem(401, { title: "Unauthorized", detail: "The login identifier or the password is wrong.", error_code: "auth.login_failed" }, "corr-login"),
    });
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<LoginPage />} />
      </Routes>,
      { route: "/login" },
    );

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText(/login identifier/i), "admin@acme.localhost");
    await user.type(screen.getByLabelText(/^password/i), "wrong");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    const alert = await screen.findByRole("alert");
    await waitFor(() => expect(alert).toHaveTextContent(/the login identifier or the password is wrong/i));
    expect(alert).toHaveTextContent("corr-login");
  });
});
