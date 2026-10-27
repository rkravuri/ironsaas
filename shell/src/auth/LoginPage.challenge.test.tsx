import { createHash, webcrypto } from "node:crypto";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { LoginPage } from "./LoginPage";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";

const salt = "5f1d2c3b4a596877?expires=4102444800";
const secret = 37;
const challenge = {
  algorithm: "SHA-256",
  challenge: createHash("sha256").update(`${salt}${secret}`).digest("hex"),
  max_number: 50,
  salt,
  signature: "a".repeat(64),
};

const decoded = (body: BodyInit | null | undefined): { challenge?: string } =>
  JSON.parse(typeof body === "string" ? body : "{}") as { challenge?: string };

beforeEach(() => {
  // The page solves with the browser's SubtleCrypto, which the test's DOM leaves out.
  vi.stubGlobal("crypto", webcrypto);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const renderLogin = () =>
  renderWithProviders(
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/" element={<p>home</p>} />
    </Routes>,
    { route: "/login" },
  );

describe("the login page's check", () => {
  it("appears only when the API asks for it, is solved in the browser, and goes with the next sign-in once [TF-072 AC4, ADR-0053]", async () => {
    let signedIn = false;
    let attempts = 0;
    const { calls } = stubFetch({
      "GET /api/v1/me": () =>
        signedIn ? json(me([])) : problem(401, { title: "Unauthorized", error_code: "auth.unauthenticated" }),
      "GET /auth/challenge": () => json(challenge),
      "POST /auth/login": (init) => {
        attempts++;
        if (decoded(init?.body).challenge === undefined) {
          return problem(403, { title: "Forbidden", error_code: "identity.login.challenge_required" });
        }
        signedIn = true;
        return json({ principal: {}, session: {} });
      },
    });
    renderLogin();
    const user = userEvent.setup();

    expect(screen.queryByRole("checkbox", { name: /i am a person/i })).toBeNull();
    await fill(user, await screen.findByLabelText(/login identifier/i), "admin@acme.localhost");
    await fill(user, screen.getByLabelText(/^password/i), "correct horse battery staple");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/tick the box below/i);
    await user.click(screen.getByRole("button", { name: /sign in/i }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/tick the box first/i);
    expect(attempts).toBe(1);

    await user.click(screen.getByRole("checkbox", { name: /i am a person/i }));
    await screen.findByText(/checked\. select sign in/i);
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    await screen.findByText("home");
    const sent = calls.filter((call) => call.key === "POST /auth/login").map((call) => decoded(call.init?.body));
    expect(sent).toHaveLength(2);
    const answer = JSON.parse(atob(sent[1]!.challenge!)) as Record<string, unknown>;
    expect(answer).toEqual({ ...challenge, number: secret, max_number: undefined });
    expect(calls.some((call) => call.key.startsWith("GET http") || call.key.includes("://"))).toBe(false);
  });

  it("asks for the box again after an answer is refused [TF-072 AC4, ADR-0053]", async () => {
    let refusals = 0;
    stubFetch({
      "GET /api/v1/me": () => problem(401, { title: "Unauthorized", error_code: "auth.unauthenticated" }),
      "GET /auth/challenge": () => json(challenge),
      "POST /auth/login": () =>
        refusals++ === 0
          ? problem(403, { title: "Forbidden", error_code: "identity.login.challenge_required" })
          : problem(403, { title: "Forbidden", error_code: "identity.login.challenge_invalid" }),
    });
    renderLogin();
    const user = userEvent.setup();
    await fill(user, await screen.findByLabelText(/login identifier/i), "admin@acme.localhost");
    await fill(user, screen.getByLabelText(/^password/i), "correct horse battery staple");
    await user.click(screen.getByRole("button", { name: /sign in/i }));
    await user.click(await screen.findByRole("checkbox", { name: /i am a person/i }));
    await screen.findByText(/checked\. select sign in/i);

    await user.click(screen.getByRole("button", { name: /sign in/i }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/expired or did not pass/i);
    await waitFor(() => expect(screen.getByRole("checkbox", { name: /i am a person/i })).not.toBeChecked());
  });

  it("says in words how long to wait when the sign-in limit is reached [TF-072 AC1, ADR-0053]", async () => {
    stubFetch({
      "GET /api/v1/me": () => problem(401, { title: "Unauthorized", error_code: "auth.unauthenticated" }),
      "POST /auth/login": () =>
        new Response(
          JSON.stringify({
            status: 429,
            title: "Too many requests.",
            error_code: "protection.rate_limited",
            retry_after_seconds: 42,
          }),
          {
            status: 429,
            headers: {
              "Content-Type": "application/problem+json",
              "Retry-After": "42",
              "X-Correlation-Id": "corr-429",
            },
          },
        ),
    });
    renderLogin();
    const user = userEvent.setup();
    await fill(user, await screen.findByLabelText(/login identifier/i), "admin@acme.localhost");
    await fill(user, screen.getByLabelText(/^password/i), "anything");

    await user.click(screen.getByRole("button", { name: /sign in/i }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Too many requests in a short time. Wait 42 seconds, then try again.");
    expect(alert).toHaveTextContent("corr-429");
  });
});
