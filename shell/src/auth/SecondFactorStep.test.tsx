import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent, { type UserEvent } from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { LoginPage } from "./LoginPage";
import { RecoveryCodesPanel } from "./mfa";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const inFiveMinutes = () => new Date(Date.now() + 5 * 60_000).toISOString();
const codes = Array.from({ length: 10 }, (_, i) => `AAAA-BBBB-CCCC-${String(i).padStart(4, "2")}`);

const pages = () =>
  renderWithProviders(
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/" element={<p>home</p>} />
      <Route path="/me" element={<p>my profile</p>} />
    </Routes>,
    { route: "/login" },
  );

async function typePassword(user: UserEvent): Promise<void> {
  await fill(user, await screen.findByLabelText(/login identifier/i), "ada@acme.localhost");
  await fill(user, screen.getByLabelText(/^password/i), "correct horse battery staple");
  await user.click(screen.getByRole("button", { name: /^sign in$/i }));
}

/** /me answers 401 until a step signs the person in; the login answers the step a test names. */
function signIn(step: { kind: string; methods: string[] }, more: Parameters<typeof stubFetch>[0]) {
  const state = { signedIn: false };
  const stubs = stubFetch({
    "GET /api/v1/me": () =>
      state.signedIn ? json(me()) : problem(401, { title: "Unauthorized", error_code: "auth.not_authenticated" }),
    "GET /auth/sign-in-options": () => json({ passkey_sign_in: false }),
    "POST /auth/login": () => json({ step: { ...step, expires_at: inFiveMinutes() } }),
    ...more,
  });
  return { state, ...stubs };
}

describe("signing in with a second factor", () => {
  it("goes from a right password to the second step with no page before it, and the app's code signs in [ADR-0047]", async () => {
    const { state, calls } = signIn(
      { kind: "second_factor", methods: ["authenticator_app", "recovery_code"] },
      {
        "POST /auth/second-factor": () => {
          state.signedIn = true;
          return json({ principal: {}, session: {} });
        },
      },
    );
    pages();
    const user = userEvent.setup();

    await typePassword(user);
    await screen.findByRole("heading", { name: "Confirm it is you" });
    expect(screen.queryByText("home")).toBeNull();
    await fill(user, screen.getByLabelText(/code from your authenticator app/i), "123456");
    await user.click(screen.getByRole("button", { name: "Continue" }));

    await screen.findByText("home");
    const proved = calls.find((c) => c.key === "POST /auth/second-factor");
    expect(JSON.parse(proved?.init?.body as string)).toEqual({ code: "123456" });
  });

  it("says how long to wait after three wrong codes in a row [TF-013 AC2]", async () => {
    signIn(
      { kind: "second_factor", methods: ["authenticator_app"] },
      {
        "POST /auth/second-factor": () =>
          problem(401, { error_code: "identity.second_factor.cooling", retry_after_seconds: 60 }, "corr-cool"),
      },
    );
    pages();
    const user = userEvent.setup();

    await typePassword(user);
    await fill(user, await screen.findByLabelText(/code from your authenticator app/i), "000000");
    await user.click(screen.getByRole("button", { name: "Continue" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Too many wrong codes in a row. Wait 60 seconds, then try again.");
    expect(alert).toHaveTextContent("corr-cool");
  });

  it("sends a person who must have a factor to enrolment, then shows the recovery codes once before any page [TF-013 AC1, ADR-0047]", async () => {
    const { state, calls } = signIn(
      { kind: "enrolment", methods: ["authenticator_app"] },
      {
        "POST /auth/enrolment/authenticator-app": () =>
          json({
            secret: "JBSWY3DPEHPK3PXP",
            uri: "otpauth://totp/IronSaaS:ada?secret=JBSWY3DPEHPK3PXP",
            expires_at: inFiveMinutes(),
          }),
        "POST /auth/enrolment/authenticator-app/confirm": () => {
          state.signedIn = true;
          return json({ principal: {}, session: {}, recovery_codes: codes });
        },
      },
    );
    pages();
    const user = userEvent.setup();

    await typePassword(user);
    await screen.findByRole("heading", { name: "Set up a second factor" });
    expect(await screen.findByRole("img", { name: "QR code for your authenticator app" })).toBeInTheDocument();
    expect(screen.getByLabelText("Key to type into the app")).toHaveValue("JBSW Y3DP EHPK 3PXP");
    await fill(user, screen.getByLabelText(/code the app shows now/i), "654321");
    await user.click(screen.getByRole("button", { name: "Save the authenticator app" }));

    const list = await screen.findByRole("list", { name: "Your recovery codes" });
    expect(within(list).getAllByRole("listitem")).toHaveLength(10);
    expect(screen.getByRole("button", { name: "Download" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Print" })).toBeInTheDocument();
    expect(screen.queryByText("home")).toBeNull();
    const go = screen.getByRole("button", { name: "Continue" });
    expect(go).toBeDisabled();
    await user.click(screen.getByLabelText("I have saved my recovery codes"));
    await user.click(go);

    await screen.findByText("home");
    const confirmed = calls.find((c) => c.key === "POST /auth/enrolment/authenticator-app/confirm");
    expect(JSON.parse(confirmed?.init?.body as string)).toEqual({ code: "654321" });
  });

  it("signs in with a recovery code and goes on to My profile to add a new factor [TF-013 AC3]", async () => {
    const { state, calls } = signIn(
      { kind: "second_factor", methods: ["authenticator_app", "recovery_code"] },
      {
        "POST /auth/second-factor": () => {
          state.signedIn = true;
          return json({ principal: {}, session: {}, enrol_new_factor: true });
        },
      },
    );
    pages();
    const user = userEvent.setup();

    await typePassword(user);
    await user.click(await screen.findByRole("button", { name: "Use a recovery code instead" }));
    await fill(user, screen.getByLabelText(/^recovery code/i), "AAAA-BBBB-CCCC-2222");
    await user.click(screen.getByRole("button", { name: "Continue" }));

    await screen.findByText("my profile");
    const proved = calls.find((c) => c.key === "POST /auth/second-factor");
    expect(JSON.parse(proved?.init?.body as string)).toEqual({ recovery_code: "AAAA-BBBB-CCCC-2222" });
  });

  it("starts again from the password when the sign-in has ended [ADR-0047]", async () => {
    signIn(
      { kind: "second_factor", methods: ["authenticator_app"] },
      { "POST /auth/second-factor": () => problem(401, { error_code: "identity.sign_in.expired" }) },
    );
    pages();
    const user = userEvent.setup();

    await typePassword(user);
    await fill(user, await screen.findByLabelText(/code from your authenticator app/i), "123456");
    await user.click(screen.getByRole("button", { name: "Continue" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("This sign-in took more than five minutes.");
    await user.click(screen.getByRole("button", { name: "Start again" }));

    await waitFor(() => expect(screen.getByLabelText(/login identifier/i)).toHaveValue("ada@acme.localhost"));
    expect(screen.getByLabelText(/^password/i)).toHaveValue("");
  });

  it("offers a passkey alone where the tenant allows it [ADR-0047]", async () => {
    stubFetch({
      "GET /api/v1/me": () => problem(401, { error_code: "auth.not_authenticated" }),
      "GET /auth/sign-in-options": () => json({ passkey_sign_in: true }),
    });
    vi.stubGlobal("PublicKeyCredential", function PublicKeyCredential() {});
    Object.defineProperty(navigator, "credentials", { value: { create: vi.fn(), get: vi.fn() }, configurable: true });
    pages();

    expect(await screen.findByRole("button", { name: "Sign in with a passkey" })).toBeInTheDocument();
  });
});

describe("recovery codes", () => {
  it("are offered to copy, download and print, and download as a text file with every code [ADR-0047]", async () => {
    const created: Blob[] = [];
    vi.spyOn(URL, "createObjectURL").mockImplementation((blob) => {
      created.push(blob as Blob);
      return "blob:codes";
    });
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined);
    const clicked = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
    const done = vi.fn();
    renderWithProviders(<RecoveryCodesPanel codes={codes} account="ada@acme.localhost" onDone={done} />);
    const user = userEvent.setup();

    expect(screen.getByRole("button", { name: "Copy the codes" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Download" }));
    const text = await created[0]!.text();

    expect(clicked).toHaveBeenCalled();
    expect(text).toContain("Recovery codes for ada@acme.localhost.");
    for (const code of codes) {
      expect(text).toContain(code);
    }
  });
});
