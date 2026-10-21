import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { fill, json, problem, renderWithProviders, stubFetch } from "@/test/render";
import { InvitationPage } from "./InvitationPage";
import { LoginPage } from "./LoginPage";
import { RecoveryPage } from "./RecoveryPage";

afterEach(() => {
  vi.unstubAllGlobals();
});

const policy = (overrides: Record<string, unknown> = {}) => ({
  minimum_length: 14,
  maximum_length: 1024,
  common_check: true,
  history_depth: 5,
  maximum_age_days: 0,
  warning_days: 14,
  ...overrides,
});

const anonymous = { "GET /api/v1/me": () => problem(401, { error_code: "auth.unauthenticated" }) };

const recovery = () =>
  renderWithProviders(
    <Routes>
      <Route path="/recovery/:token" element={<RecoveryPage />} />
      <Route path="/login" element={<LoginPage autoFocus={false} />} />
    </Routes>,
    { route: "/recovery/the-code" },
  );

const setPassword = async (password: string) => {
  const user = userEvent.setup();
  await fill(user, await screen.findByLabelText(/^new password/i), password);
  await fill(user, screen.getByLabelText(/repeat the new password/i), password);
  await user.click(screen.getByRole("button", { name: "Save the new password" }));
};

describe("the password forms", () => {
  it("say the tenant's policy in words before anyone types, and mark the rule a refusal names [TF-012 AC1, ADR-0046]", async () => {
    stubFetch({
      ...anonymous,
      "GET /auth/password-policy": () => json(policy()),
      "POST /auth/recovery/the-code": () =>
        problem(400, {
          error_code: "identity.password.common",
          detail: "This password is on the list of the most common passwords.",
          errors: [{ field: "password", code: "common", message: "Choose a password that is not common." }],
        }),
    });
    recovery();

    const rules = await screen.findByRole("list", { name: "Your password needs" });
    await waitFor(() => expect(rules).toHaveTextContent("At least 14 characters."));
    expect(rules).toHaveTextContent("Not one of the most common passwords");
    expect(rules).toHaveTextContent("Not your login identifier.");
    expect(rules).toHaveTextContent("Not one of your last 5 passwords.");
    expect(screen.getByText(/no rules about kinds of characters/)).toBeInTheDocument();

    await setPassword("qwertyuiop1234");

    const failed = await waitFor(() => {
      const item = rules.querySelector("[data-failed=true]");
      expect(item).not.toBeNull();
      return item as HTMLElement;
    });
    expect(failed).toHaveAttribute("data-rule", "common");
    expect(failed).toHaveTextContent("Your password did not meet this rule.");
    expect(screen.getByText("Choose a password that is not common.")).toBeInTheDocument();
  });

  it("check the tenant's minimum length before sending [TF-012 AC4, ADR-0046]", async () => {
    const { calls } = stubFetch({
      ...anonymous,
      "GET /auth/password-policy": () => json(policy({ minimum_length: 16 })),
    });
    recovery();
    await waitFor(() => expect(screen.getByText("At least 16 characters.")).toBeInTheDocument());

    await setPassword("fifteen letters");

    expect(await screen.findByText("Use at least 16 characters.")).toBeInTheDocument();
    expect(calls.some((call) => call.key.startsWith("POST /auth/recovery/"))).toBe(false);
  });

  it("save a new password once with a recovery link and say every session ended [TF-017 AC1, ADR-0046]", async () => {
    const { calls } = stubFetch({
      ...anonymous,
      "GET /auth/password-policy": () => json(policy()),
      "POST /auth/recovery/the-code": () => json({ id: "u1" }),
    });
    recovery();

    await setPassword("a brand new passphrase");

    expect(await screen.findByText(/every session of your account has ended/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Sign in" })).toHaveAttribute("href", "/login");
    const sent = calls.find((call) => call.key === "POST /auth/recovery/the-code");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({ password: "a brand new passphrase" });
  });

  it("say a used, replaced or expired recovery link does not work and to ask the administrator [TF-017 AC2, ADR-0046]", async () => {
    stubFetch({
      ...anonymous,
      "GET /auth/password-policy": () => json(policy()),
      "POST /auth/recovery/the-code": () => problem(404, { error_code: "identity.recovery.invalid" }, "corr-used"),
    });
    recovery();

    await setPassword("a brand new passphrase");

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("This recovery link does not work any more");
    expect(alert).toHaveTextContent("Ask your administrator for a new one.");
    expect(alert).toHaveTextContent("corr-used");
  });

  it("keep the invitation's form when the policy refuses the password, rather than calling the invitation invalid [TF-012 AC1, TF-018 AC1]", async () => {
    stubFetch({
      ...anonymous,
      "GET /auth/password-policy": () => json(policy()),
      "POST /auth/invitations/inv/accept": () =>
        problem(400, {
          error_code: "identity.password.is_login_id",
          errors: [{ field: "password", code: "is_login_id", message: "Choose a password that is not your login." }],
        }),
    });
    renderWithProviders(
      <Routes>
        <Route path="/invitations/:token" element={<InvitationPage />} />
      </Routes>,
      { route: "/invitations/inv" },
    );

    const rules = await screen.findByRole("list", { name: "Your password needs" });
    expect(rules).not.toHaveTextContent("Not one of your last"); // a new account has no earlier passwords
    const user = userEvent.setup();
    await fill(user, screen.getByLabelText(/^password/i), "ada@acme.localhost");
    await fill(user, screen.getByLabelText(/repeat the password/i), "ada@acme.localhost");
    await user.click(screen.getByRole("button", { name: "Activate the account" }));

    expect(await screen.findByText("Choose a password that is not your login.")).toBeInTheDocument();
    expect(
      within(rules)
        .getByText(/Not your login identifier/)
        .closest("[data-rule]"),
    ).toHaveAttribute("data-failed", "true");
    expect(screen.queryByText(/not valid any more/)).toBeNull();
  });
});

describe("the sign-in page and passwords", () => {
  const signIn = async () => {
    const user = userEvent.setup();
    await fill(user, await screen.findByLabelText(/login identifier/i), "ada@acme.localhost");
    await fill(user, screen.getByLabelText(/^password/i), "the password");
    await user.click(screen.getByRole("button", { name: /sign in/i }));
  };

  it("says the login is locked with words that are the same for every identifier [TF-012 AC2, TF-011 AC2, ADR-0046]", async () => {
    stubFetch({
      ...anonymous,
      "POST /auth/login": () =>
        problem(
          401,
          { error_code: "identity.login.locked", detail: "Too many failed sign-ins for this login." },
          "corr-lock",
        ),
    });
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<LoginPage autoFocus={false} />} />
      </Routes>,
      { route: "/login" },
    );

    await signIn();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(
      /^Too many failed sign-ins for this login\. Try again later, or ask your administrator to unlock it\./,
    );
    expect(alert).toHaveTextContent("corr-lock");
  });

  it("sends a right password past its maximum age to the recovery page with the code the API gave [TF-012 AC5, ADR-0046]", async () => {
    stubFetch({
      ...anonymous,
      "GET /auth/password-policy": () => json(policy()),
      "POST /auth/login": () =>
        problem(401, { error_code: "identity.password.expired", recovery_token: "expired-code" }),
    });
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<LoginPage autoFocus={false} />} />
        <Route path="/recovery/:token" element={<RecoveryPage />} />
      </Routes>,
      { route: "/login" },
    );

    await signIn();

    expect(await screen.findByRole("heading", { name: "Choose a new password" })).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Your password has expired. Choose a new one to sign in.");
  });

  it("says to ask the administrator for a recovery link when the password is forgotten, until email exists [ADR-0046]", async () => {
    stubFetch(anonymous);
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<LoginPage autoFocus={false} />} />
      </Routes>,
      { route: "/login" },
    );

    const forgot = await screen.findByText("Forgot your password?");
    expect(forgot.closest("[data-forgot]")).toHaveTextContent(
      "Ask your administrator for a recovery link. It works once, for 15 minutes.",
    );
  });
});
