import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ReauthenticationForm } from "./Reauthentication";
import { leaveFor } from "./leave";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";

vi.mock("./leave", () => ({ leaveFor: vi.fn() }));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(leaveFor).mockReset();
});

const contoso = { id: "p1", name: "Contoso Entra ID" };
const authorization = "https://login.example.test/authorize?state=s&prompt=login&max_age=0";

describe("confirming it is you without a password", () => {
  it("sends a person who signs in through a company login to a fresh sign-in there, back to this page [TF-248 AC6, ADR-0048]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "POST /api/v1/me/reauthentication": () =>
        json({ second_factor: false, methods: [], passkey_options: null, password: false, provider: contoso }),
      "POST /api/v1/me/reauthentication/sso": () =>
        json({ authorization_url: authorization, expires_at: "2026-10-22T10:05:00Z" }),
    });
    renderWithProviders(<ReauthenticationForm onConfirmed={() => undefined} onCancel={() => undefined} />);
    const user = userEvent.setup();

    expect(await screen.findByText(/You sign in with Contoso Entra ID\./)).toBeInTheDocument();
    expect(screen.queryByLabelText(/^password/i)).toBeNull();
    await user.click(screen.getByRole("button", { name: "Sign in again with Contoso Entra ID" }));

    await waitFor(() => expect(leaveFor).toHaveBeenCalledWith(authorization));
    const start = calls.find((c) => c.key === "POST /api/v1/me/reauthentication/sso");
    expect(JSON.parse(start?.init?.body as string)).toEqual({ return_to: "/" });
  });

  it("asks a person with a second factor here for it first, and says why when it is wrong [TF-248 AC2, TF-248 AC6, ADR-0048]", async () => {
    let tries = 0;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "POST /api/v1/me/reauthentication": () =>
        json({
          second_factor: true,
          methods: ["authenticator_app", "recovery_code"],
          passkey_options: null,
          password: false,
          provider: contoso,
        }),
      "POST /api/v1/me/reauthentication/sso": () =>
        ++tries === 1
          ? problem(
              403,
              { error_code: "identity.second_factor.invalid", detail: "That code did not work." },
              "corr-code",
            )
          : json({ authorization_url: authorization, expires_at: "2026-10-22T10:05:00Z" }),
    });
    renderWithProviders(<ReauthenticationForm onConfirmed={() => undefined} onCancel={() => undefined} />);
    const user = userEvent.setup();

    expect(
      await screen.findByText(/give your second factor here, then sign in with Contoso Entra ID again/),
    ).toBeInTheDocument();
    await fill(user, await screen.findByLabelText(/code from your authenticator app/i), "000000");
    await user.click(screen.getByRole("button", { name: "Sign in again with Contoso Entra ID" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("corr-code");
    expect(leaveFor).not.toHaveBeenCalled();
    await user.clear(screen.getByLabelText(/code from your authenticator app/i));
    await fill(user, screen.getByLabelText(/code from your authenticator app/i), "123456");
    await user.click(screen.getByRole("button", { name: "Sign in again with Contoso Entra ID" }));

    await waitFor(() => expect(leaveFor).toHaveBeenCalledWith(authorization));
    const last = calls.filter((c) => c.key === "POST /api/v1/me/reauthentication/sso").at(-1);
    expect(JSON.parse(last?.init?.body as string)).toEqual({ return_to: "/", code: "123456" });
  });
});
