import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { LoginPage } from "./LoginPage";
import { ReauthenticationNotice } from "./ReauthenticationNotice";
import { leaveFor } from "./leave";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";

vi.mock("./leave", () => ({ leaveFor: vi.fn() }));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(leaveFor).mockReset();
});

const signedOut = () => problem(401, { title: "Unauthorized", error_code: "auth.not_authenticated" });
const contoso = { id: "11111111-1111-7111-8111-111111111111", name: "Contoso Entra ID" };
const authorization = "https://login.example.test/authorize?state=s&nonce=n&code_challenge=c";

function page(route: string) {
  renderWithProviders(
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/sso/callback" element={<LoginPage callback />} />
      <Route path="/" element={<p>home</p>} />
      <Route path="/users" element={<p>users page</p>} />
      <Route
        path="/me"
        element={
          <>
            <ReauthenticationNotice />
            <p>my profile</p>
          </>
        }
      />
    </Routes>,
    { route },
  );
}

describe("the login page with company logins", () => {
  it("offers a button per company login, which sends the browser to the provider with where the person was going [TF-014 AC1, ADR-0048]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": signedOut,
      "GET /auth/sign-in-options": () => json({ passkey_sign_in: false, providers: [contoso] }),
      "POST /auth/sso/start": () => json({ authorization_url: authorization, expires_at: "2026-10-22T10:05:00Z" }),
    });
    page("/login");
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Continue with Contoso Entra ID" }));

    await waitFor(() => expect(leaveFor).toHaveBeenCalledWith(authorization));
    const start = calls.find((c) => c.key === "POST /auth/sso/start");
    expect(JSON.parse(start?.init?.body as string)).toEqual({
      provider_id: contoso.id,
      return_to: "/",
      login_hint: null,
    });
  });

  it("sends a work email typed without a password to its domain's company login, and asks for the password otherwise [TF-014 AC1, ADR-0048]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": signedOut,
      "GET /auth/sign-in-options": () => json({ passkey_sign_in: false, providers: [contoso] }),
      "POST /auth/sso/discover": (init) =>
        json({
          provider: (JSON.parse(init?.body as string) as { email: string }).email.endsWith("@contoso.test")
            ? contoso
            : null,
        }),
      "POST /auth/sso/start": () => json({ authorization_url: authorization, expires_at: "2026-10-22T10:05:00Z" }),
    });
    page("/login");
    const user = userEvent.setup();
    const loginId = await screen.findByLabelText(/login identifier/i);
    await screen.findByRole("button", { name: "Continue with Contoso Entra ID" });

    await fill(user, loginId, "someone@elsewhere.test");
    await user.click(screen.getByRole("button", { name: "Sign in" }));
    expect(
      await screen.findByText(/Type your password\. No company login is set up for that email here\./),
    ).toBeInTheDocument();
    await user.clear(loginId);
    await fill(user, loginId, "grace@contoso.test");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(leaveFor).toHaveBeenCalledWith(authorization));
    const start = calls.find((c) => c.key === "POST /auth/sso/start");
    expect(JSON.parse(start?.init?.body as string)).toMatchObject({
      provider_id: contoso.id,
      login_hint: "grace@contoso.test",
    });
    expect(calls.some((c) => c.key === "POST /auth/login")).toBe(false);
  });

  it("says Use your company login to a person who signs in only through one, with its button [TF-014 AC2, ADR-0048]", async () => {
    stubFetch({
      "GET /api/v1/me": signedOut,
      "POST /auth/login": () =>
        problem(
          401,
          {
            title: "Unauthorized",
            detail: "Use your company login.",
            error_code: "identity.login.use_company_login",
            provider_id: contoso.id,
            provider_name: contoso.name,
          },
          "corr-sso",
        ),
      "POST /auth/sso/start": () => json({ authorization_url: authorization, expires_at: "2026-10-22T10:05:00Z" }),
    });
    page("/login");
    const user = userEvent.setup();

    await fill(user, await screen.findByLabelText(/login identifier/i), "ada@contoso.test");
    await fill(user, screen.getByLabelText(/^password/i), "an old password");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/^Use your company login\./);
    await user.click(screen.getByRole("button", { name: "Continue with Contoso Entra ID" }));
    await waitFor(() => expect(leaveFor).toHaveBeenCalledWith(authorization));
  });

  it("completes the sign-in the company login sent back, and goes where the person was going [TF-014 AC1, ADR-0048]", async () => {
    let signedIn = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => (signedIn ? json(me()) : signedOut()),
      "POST /auth/sso/callback": () => {
        signedIn = true;
        return json({ principal: {}, session: {}, return_to: "/users" });
      },
    });
    page("/sso/callback?code=the-code&state=the-state");

    expect(await screen.findByText("users page")).toBeInTheDocument();
    const callback = calls.find((c) => c.key === "POST /auth/sso/callback");
    expect(JSON.parse(callback?.init?.body as string)).toEqual({
      code: "the-code",
      state: "the-state",
      error: null,
      error_description: null,
    });
    expect(calls.filter((c) => c.key === "POST /auth/sso/callback")).toHaveLength(1);
  });

  it("takes a refused company sign-in back to the sign-in page with the reason [TF-014 AC3, ADR-0048]", async () => {
    stubFetch({
      "GET /api/v1/me": signedOut,
      "POST /auth/sso/callback": () =>
        problem(
          403,
          {
            title: "Forbidden",
            detail:
              "Contoso Entra ID signed you in as mallory@elsewhere.test, but this organisation does not sign people of elsewhere.test in through it. Ask your administrator.",
            error_code: "identity.sso.domain_not_allowed",
          },
          "corr-domain",
        ),
    });
    page("/sso/callback?code=c&state=s");

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(/does not sign people of elsewhere\.test in through it/);
    expect(alert).toHaveTextContent("corr-domain");
    expect(screen.getByLabelText(/login identifier/i)).toBeInTheDocument();
  });

  it("goes on to this product's second factor when the company login is not trusted for it [TF-014 AC5, ADR-0048]", async () => {
    stubFetch({
      "GET /api/v1/me": signedOut,
      "POST /auth/sso/callback": () =>
        json({ step: { kind: "second_factor", methods: ["authenticator_app"], expires_at: "2026-10-22T10:05:00Z" } }),
    });
    page("/sso/callback?code=c&state=s");

    expect(await screen.findByLabelText(/code from your authenticator app/i)).toBeInTheDocument();
  });

  it("comes back to the page with the action ready to do again, once the company login confirmed it is the person [TF-248 AC6, ADR-0048]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "POST /auth/sso/callback": () => json({ reauthenticated: true, return_to: "/me" }),
    });
    page("/sso/callback?code=c&state=s");

    expect(await screen.findByText("my profile")).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("You confirmed it is you. Do the action again now");
    expect(calls.filter((c) => c.key === "POST /auth/sso/callback")).toHaveLength(1);
  });

  it("comes back to the page saying the company login did not ask for a new sign-in, the action not done [TF-248 AC6, ADR-0048]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "POST /auth/sso/callback": () =>
        problem(
          403,
          {
            title: "Forbidden",
            detail:
              "Contoso Entra ID did not ask you to sign in again, so it did not confirm it is you, and the action was not done. Try again; if this repeats, ask your administrator to check the company login.",
            error_code: "identity.sso.reauthentication_stale",
            return_to: "/me",
          },
          "corr-stale",
        ),
    });
    page("/sso/callback?code=c&state=s");

    expect(await screen.findByText("my profile")).toBeInTheDocument();
    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent(/did not ask you to sign in again/);
    expect(alert).toHaveTextContent("corr-stale");
  });
});
