import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { LoginPage } from "./LoginPage";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the login page", () => {
  it("signs in, re-reads /me and moves on [TF-011 AC1, ADR-0020, ADR-0033]", async () => {
    let signedIn = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () =>
        signedIn
          ? json(me(["identity.users.read"]))
          : problem(401, { title: "Unauthorized", error_code: "auth.unauthenticated" }),
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
    await fill(user, await screen.findByLabelText(/login identifier/i), "admin@acme.localhost");
    await fill(user, screen.getByLabelText(/^password/i), "correct horse battery staple");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    await screen.findByText("home");
    expect(calls.some((c) => c.key === "POST /auth/login")).toBe(true);
  });

  it("shows the fixed message, The login details are not correct, for every failure, with the correlation id when there is one [TF-011 AC2, ADR-0020]", async () => {
    const answers = [
      problem(
        401,
        { title: "Unauthorized", detail: "The login details are not correct.", error_code: "auth.login_failed" },
        "corr-login",
      ),
      problem(
        400,
        { title: "Bad Request", detail: "One or more fields are not valid.", error_code: "api.validation" },
        "corr-bad",
      ),
      problem(500, { title: "Internal Server Error", error_code: "api.unexpected" }, "corr-server"),
    ];
    let next = 0;
    stubFetch({
      "GET /api/v1/me": () => problem(401, { title: "Unauthorized", error_code: "auth.unauthenticated" }),
      "POST /auth/login": () => answers[next++]!,
    });
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<LoginPage />} />
      </Routes>,
      { route: "/login" },
    );

    const user = userEvent.setup();
    await fill(user, await screen.findByLabelText(/login identifier/i), "admin@acme.localhost");
    await fill(user, screen.getByLabelText(/^password/i), "wrong");
    for (const correlation of ["corr-login", "corr-bad", "corr-server"]) {
      await user.click(screen.getByRole("button", { name: /sign in/i }));
      const alert = await screen.findByRole("alert");
      await waitFor(() => expect(alert).toHaveTextContent(correlation));
      expect(alert).toHaveTextContent(/^The login details are not correct\./);
      expect(alert).not.toHaveTextContent(/wrong|not active|valid|unexpected/i);
    }
  });
});
