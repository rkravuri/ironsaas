import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { fill, json, me, renderWithProviders, stubFetch } from "@/test/render";
import { SecondFactors } from "./SecondFactors";

afterEach(() => {
  vi.unstubAllGlobals();
});

const factors = (overrides: Record<string, unknown> = {}) => ({
  factors: [
    { id: "f1", kind: "authenticator_app", name: "Phone", created_at: "2026-10-01T09:00:00Z", last_used_at: null },
    {
      id: "f2",
      kind: "passkey",
      name: "Laptop",
      created_at: "2026-10-02T09:00:00Z",
      last_used_at: "2026-10-20T09:00:00Z",
    },
  ],
  recovery_codes_left: 7,
  required: true,
  allowed: ["authenticator_app", "passkey"],
  enrol_by: null,
  ...overrides,
});

const page = (route = "/me?tab=security") =>
  renderWithProviders(
    <Routes>
      <Route path="/me" element={<SecondFactors />} />
    </Routes>,
    { route },
  );

describe("My profile's second factors", () => {
  it("lists the factors, renames and removes one, and shows new recovery codes once [ADR-0047]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/me/factors": () => json(factors()),
      "PATCH /api/v1/me/factors/f2": () =>
        json({
          id: "f2",
          kind: "passkey",
          name: "Work laptop",
          created_at: "2026-10-02T09:00:00Z",
          last_used_at: null,
        }),
      "DELETE /api/v1/me/factors/f1": () => new Response(null, { status: 204 }),
      "POST /api/v1/me/recovery-codes": () =>
        json({ codes: Array.from({ length: 10 }, (_, i) => `NEWC-ODES-HERE-${String(i).padStart(4, "2")}`) }),
    });
    page();
    const user = userEvent.setup();

    const table = await screen.findByRole("table", { name: "Your second factors" });
    expect(within(table).getAllByRole("row")).toHaveLength(3);
    expect(within(table).getByText("Authenticator app")).toBeInTheDocument();
    expect(screen.getByText("You have 7 recovery codes left.")).toBeInTheDocument();
    expect(screen.getByText("Your organisation requires a second factor of you.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Rename Laptop" }));
    const name = await screen.findByLabelText(/^name/i);
    await user.clear(name);
    await fill(user, name, "Work laptop");
    await user.click(screen.getByRole("button", { name: "Save the name" }));
    await waitFor(() => expect(calls.some((c) => c.key === "PATCH /api/v1/me/factors/f2")).toBe(true));
    const renamed = calls.find((c) => c.key === "PATCH /api/v1/me/factors/f2");
    expect(JSON.parse(renamed?.init?.body as string)).toEqual({ name: "Work laptop" });

    await user.click(await screen.findByRole("button", { name: "Remove Phone" }));
    await user.click(
      within(await screen.findByRole("dialog", { name: "Remove Phone?" })).getByRole("button", { name: "Remove" }),
    );
    await waitFor(() => expect(calls.some((c) => c.key === "DELETE /api/v1/me/factors/f1")).toBe(true));

    await user.click(screen.getByRole("button", { name: "Make new recovery codes" }));
    await user.click(
      within(await screen.findByRole("dialog", { name: "Make new recovery codes?" })).getByRole("button", {
        name: "Make new codes",
      }),
    );
    const shown = await screen.findByRole("list", { name: "Your recovery codes" });
    expect(within(shown).getAllByRole("listitem")).toHaveLength(10);
  });

  it("opens Add a second factor after a sign-in by recovery code, and an authenticator app joins the list [TF-013 AC3]", async () => {
    let added = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/me/factors": () =>
        json(
          added
            ? factors({ factors: [factors().factors[1]], recovery_codes_left: 9 })
            : factors({ factors: [], recovery_codes_left: 9 }),
        ),
      "POST /api/v1/me/factors/authenticator-app": () =>
        json({
          secret: "JBSWY3DPEHPK3PXP",
          uri: "otpauth://totp/IronSaaS:ada?secret=JBSWY3DPEHPK3PXP",
          expires_at: "2026-10-22T10:05:00Z",
        }),
      "POST /api/v1/me/factors/authenticator-app/confirm": () => {
        added = true;
        return json({
          factor: {
            id: "f3",
            kind: "authenticator_app",
            name: "New phone",
            created_at: "2026-10-22T10:00:00Z",
            last_used_at: null,
          },
          recovery_codes: null,
        });
      },
    });
    page("/me?tab=security&add=1");
    const user = userEvent.setup();

    expect(await screen.findByText(/You signed in with a recovery code\./)).toBeInTheDocument();
    const dialog = await screen.findByRole("dialog", { name: "Add a second factor" });
    await user.click(within(dialog).getByText("Authenticator app"));
    expect(await within(dialog).findByRole("img", { name: "QR code for your authenticator app" })).toBeInTheDocument();
    await fill(user, within(dialog).getByLabelText(/^name/i), "New phone");
    await fill(user, within(dialog).getByLabelText(/code the app shows now/i), "123456");
    await user.click(within(dialog).getByRole("button", { name: "Save the authenticator app" }));

    expect(await screen.findByText("Your second factor is saved.")).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    const confirmed = calls.find((c) => c.key === "POST /api/v1/me/factors/authenticator-app/confirm");
    expect(JSON.parse(confirmed?.init?.body as string)).toEqual({ code: "123456", name: "New phone" });
  });
});
