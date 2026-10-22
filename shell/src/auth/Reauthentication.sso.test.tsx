import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ReauthenticationForm } from "./Reauthentication";
import { leaveFor } from "./leave";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";

vi.mock("./leave", () => ({ leaveFor: vi.fn() }));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(leaveFor).mockReset();
});

describe("confirming it is you without a password", () => {
  it("sends a person who signs in through a company login to sign in through it again, back to this page [TF-248 AC2, ADR-0048]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "POST /api/v1/me/reauthentication": () =>
        json({
          second_factor: false,
          methods: [],
          passkey_options: null,
          password: false,
          provider: { id: "p1", name: "Contoso Entra ID" },
        }),
      "POST /auth/sso/start": () =>
        json({ authorization_url: "https://login.example.test/authorize?state=s", expires_at: "2026-10-22T10:05:00Z" }),
    });
    renderWithProviders(<ReauthenticationForm onConfirmed={() => undefined} onCancel={() => undefined} />);
    const user = userEvent.setup();

    expect(await screen.findByText(/You sign in with Contoso Entra ID\./)).toBeInTheDocument();
    expect(screen.queryByLabelText(/^password/i)).toBeNull();
    await user.click(screen.getByRole("button", { name: "Sign in again with Contoso Entra ID" }));

    await waitFor(() => expect(leaveFor).toHaveBeenCalledWith("https://login.example.test/authorize?state=s"));
    const start = calls.find((c) => c.key === "POST /auth/sso/start");
    expect(JSON.parse(start?.init?.body as string)).toMatchObject({ provider_id: "p1", return_to: "/" });
  });
});
