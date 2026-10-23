import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { SigningDialog } from "./SigningDialog";
import { leaveFor } from "./leave";
import { pendingSigning } from "./signing-resume";
import { fill, json, me, renderWithProviders, stubFetch } from "@/test/render";

vi.mock("./leave", () => ({ leaveFor: vi.fn() }));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(leaveFor).mockClear();
  window.sessionStorage.clear();
});

describe("a company-login signature on its way to the provider", () => {
  it("keeps the record, the meaning and the comment for the way back, then leaves for the fresh sign-in [TF-063 AC5, ADR-0051]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/signatures/challenge?record_type=access_review_decision": () =>
        json({
          first_in_session: false,
          login_id: false,
          password: false,
          second_factor: false,
          methods: [],
          passkey_options: null,
          provider_id: "p1",
          provider_name: "Acme Login",
          provider_confirmed: false,
          reauthenticate: true,
          policy: { required: true, meanings: ["approved", "rejected"], reauthenticate: true },
        }),
      "POST /api/v1/me/reauthentication/sso": () =>
        json({ authorization_url: "https://login.acme.test/authorize?state=s1", expires_at: "2026-10-23T10:10:00Z" }),
    });
    renderWithProviders(
      <SigningDialog
        opened
        recordType="access_review_decision"
        title="Sign the revocation"
        summary="Marie Curie loses Store manager."
        meanings={["rejected"]}
        context={{ item: { id: "item-7" }, decision: "revoke" }}
        onSign={() => Promise.resolve()}
        onClose={() => undefined}
      />,
    );
    const user = userEvent.setup();

    await fill(user, await screen.findByLabelText(/^comment/i), "Left the team.");
    await user.click(screen.getByRole("button", { name: "Sign in again at Acme Login" }));

    await waitFor(() => expect(leaveFor).toHaveBeenCalledWith("https://login.acme.test/authorize?state=s1"));
    expect(
      JSON.parse(calls.find((c) => c.key === "POST /api/v1/me/reauthentication/sso")?.init?.body as string),
    ).toEqual({
      return_to: "/",
    });
    expect(pendingSigning("access_review_decision")).toMatchObject({
      meaning: "rejected",
      comment: "Left the team.",
      context: { item: { id: "item-7" }, decision: "revoke" },
    });
    expect(pendingSigning("audit_trail_review")).toBeUndefined();
  });
});
