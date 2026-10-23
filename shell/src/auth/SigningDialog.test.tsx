import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "@/api/client";
import { SigningDialog } from "./SigningDialog";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";

afterEach(() => {
  vi.unstubAllGlobals();
});

const challengePath = "GET /api/v1/signatures/challenge?record_type=sample_document";
const policy = { required: true, meanings: ["approved", "rejected"], reauthenticate: true };

const challenge = (overrides: Record<string, unknown> = {}) => ({
  first_in_session: true,
  login_id: true,
  password: true,
  second_factor: false,
  methods: [],
  passkey_options: null,
  provider_id: null,
  provider_name: null,
  provider_confirmed: false,
  reauthenticate: true,
  policy,
  ...overrides,
});

function open(onSign: Parameters<typeof SigningDialog>[0]["onSign"] = vi.fn(() => Promise.resolve())) {
  renderWithProviders(
    <SigningDialog
      opened
      recordType="sample_document"
      title="Sign the document"
      summary="You approve the supply agreement."
      meanings={["approved", "rejected"]}
      onSign={onSign}
      onClose={() => undefined}
    />,
  );
  return onSign;
}

describe("the signing dialog", () => {
  it("asks for the login identifier and the password at the first signature of a session, with the meaning and a comment [TF-063 AC4, ADR-0051]", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me()), [challengePath]: () => json(challenge()) });
    const onSign = open();
    const user = userEvent.setup();

    const dialog = await screen.findByRole("dialog", { name: "Sign the document" });
    expect(dialog).toHaveTextContent("You approve the supply agreement.");
    expect(await screen.findByText(/first signature since you signed in/i)).toBeInTheDocument();
    await user.click(screen.getByRole("radio", { name: "Rejected" }));
    await fill(user, screen.getByLabelText(/^login identifier/i), " admin@acme.localhost ");
    await fill(user, screen.getByLabelText(/^password/i), "correct horse battery staple");
    await fill(user, screen.getByLabelText(/^comment/i), "Left the team.");
    await user.click(screen.getByRole("button", { name: "Sign" }));

    await waitFor(() =>
      expect(onSign).toHaveBeenCalledWith(
        { login_id: "admin@acme.localhost", password: "correct horse battery staple" },
        "rejected",
        "Left the team.",
      ),
    );
    expect(screen.queryByLabelText(/code from your authenticator app/i)).toBeNull();
  });

  it("asks for the password alone at a later signature, and the second factor only when the API says so [TF-063 AC4, ADR-0051]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      [challengePath]: () =>
        json(
          challenge({ first_in_session: false, login_id: false, second_factor: true, methods: ["authenticator_app"] }),
        ),
    });
    const onSign = open();
    const user = userEvent.setup();

    await screen.findByText("Type your password to sign.");
    expect(screen.queryByLabelText(/^login identifier/i)).toBeNull();
    await fill(user, screen.getByLabelText(/^password/i), "correct horse battery staple");
    await fill(user, screen.getByLabelText(/code from your authenticator app/i), "123456");
    await user.click(screen.getByRole("button", { name: "Sign" }));

    await waitFor(() =>
      expect(onSign).toHaveBeenCalledWith(
        { password: "correct horse battery staple", code: "123456" },
        "approved",
        undefined,
      ),
    );
  });

  it("shows why a refused signature was refused, with its correlation id, and keeps the dialog [TF-063 AC1, ADR-0051]", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me()), [challengePath]: () => json(challenge()) });
    open(() =>
      Promise.reject(
        new ApiError(403, { error_code: "compliance.signature.invalid", status: 403, title: "Forbidden" }, "corr-sign"),
      ),
    );
    const user = userEvent.setup();

    await fill(user, await screen.findByLabelText(/^login identifier/i), "admin@acme.localhost");
    await fill(user, screen.getByLabelText(/^password/i), "not the password");
    await user.click(screen.getByRole("button", { name: "Sign" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("That did not confirm it is you.");
    expect(alert).toHaveTextContent("corr-sign");
    expect(screen.getByRole("dialog", { name: "Sign the document" })).toBeInTheDocument();
  });

  it("sends a company-login person to sign in again there, then lets them sign with nothing typed [TF-063 AC5, ADR-0048, ADR-0051]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      [challengePath]: () =>
        json(
          challenge({
            login_id: false,
            password: false,
            provider_id: "p1",
            provider_name: "Acme Login",
            provider_confirmed: true,
          }),
        ),
    });
    const onSign = open();
    const user = userEvent.setup();

    expect(await screen.findByText("Acme Login confirmed it is you. Sign now.")).toBeInTheDocument();
    expect(screen.queryByLabelText(/^password/i)).toBeNull();
    await user.click(screen.getByRole("button", { name: "Sign" }));

    await waitFor(() => expect(onSign).toHaveBeenCalledWith({}, "approved", undefined));
  });

  it("offers the fresh sign-in at the company login before it has happened [TF-063 AC5, ADR-0051]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      [challengePath]: () =>
        json(challenge({ login_id: false, password: false, provider_id: "p1", provider_name: "Acme Login" })),
    });
    open();

    expect(await screen.findByRole("button", { name: "Sign in again at Acme Login" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sign" })).toBeNull();
  });

  it("tells someone signed in as another person that they cannot sign, and asks for nothing [TF-063 AC6, ADR-0051]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      [challengePath]: () => problem(403, { error_code: "compliance.signature.impersonation" }, "corr-imp"),
    });
    open();

    expect(await screen.findByRole("alert")).toHaveTextContent("You are signed in as someone else, so you cannot sign");
    expect(screen.queryByLabelText(/^password/i)).toBeNull();
    expect(screen.queryByRole("button", { name: "Sign" })).toBeNull();
  });
});
