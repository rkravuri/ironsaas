import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Button, Text } from "@mantine/core";
import { request } from "@/api/client";
import { ReauthenticationDialog } from "./Reauthentication";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";

afterEach(() => {
  vi.unstubAllGlobals();
});

/** A page with a high-risk action, as any page of the shell has, and the shell's one dialog beside it. */
function HighRiskPage() {
  const [done, setDone] = useState<string | undefined>();
  return (
    <>
      <Button
        onClick={() =>
          void request("/api/v1/users/u1/recovery-link", { method: "POST" }).then(
            () => setDone("issued"),
            () => setDone("refused"),
          )
        }
      >
        Issue a recovery link
      </Button>
      {done && <Text>{done}</Text>}
      <ReauthenticationDialog />
    </>
  );
}

describe("the re-authentication dialog", () => {
  it("opens when an action is refused for an old sign-in, asks the password and the code, and the action goes on [TF-248 AC2, TF-248 AC5]", async () => {
    let issued = 0;
    let confirmations = 0;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "POST /api/v1/users/u1/recovery-link": () =>
        ++issued === 1
          ? problem(403, { error_code: "auth.reauthentication_required" })
          : json({ recovery_link: "/recovery#code", expires_at: "2026-10-22T10:15:00Z" }),
      "POST /api/v1/me/reauthentication": () =>
        json({ second_factor: true, methods: ["authenticator_app", "recovery_code"], passkey_options: null }),
      "POST /api/v1/me/reauthenticate": () =>
        ++confirmations === 1
          ? problem(403, { error_code: "identity.reauthentication.invalid" }, "corr-reauth")
          : json({ authenticated_at: "2026-10-22T10:00:00Z" }),
    });
    renderWithProviders(<HighRiskPage />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Issue a recovery link" }));
    const dialog = await screen.findByRole("dialog", { name: "Confirm it is you" });
    await fill(user, await screen.findByLabelText(/^password/i), "not the password");
    await fill(user, await screen.findByLabelText(/code from your authenticator app/i), "123456");
    await user.click(screen.getByRole("button", { name: "Confirm" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("That did not confirm it is you.");
    expect(dialog).toBeInTheDocument(); // a wrong answer keeps the dialog, and the action waits

    await user.clear(screen.getByLabelText(/^password/i));
    await fill(user, screen.getByLabelText(/^password/i), "correct horse battery staple");
    await user.click(screen.getByRole("button", { name: "Confirm" }));

    await screen.findByText("issued");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    const confirmed = calls.filter((c) => c.key === "POST /api/v1/me/reauthenticate").at(-1);
    expect(JSON.parse(confirmed?.init?.body as string)).toEqual({
      password: "correct horse battery staple",
      code: "123456",
    });
    expect(issued).toBe(2);
  });

  it("lets the refusal through when the person cancels, and nothing is sent again [TF-248 AC5]", async () => {
    let issued = 0;
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "POST /api/v1/users/u1/recovery-link": () => {
        issued++;
        return problem(403, { error_code: "auth.reauthentication_required" });
      },
      "POST /api/v1/me/reauthentication": () => json({ second_factor: false, methods: [], passkey_options: null }),
    });
    renderWithProviders(<HighRiskPage />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Issue a recovery link" }));
    await screen.findByRole("dialog", { name: "Confirm it is you" });
    expect(screen.queryByLabelText(/code from your authenticator app/i)).toBeNull(); // no factor, no code asked
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    await screen.findByText("refused");
    expect(issued).toBe(1);
  });
});
