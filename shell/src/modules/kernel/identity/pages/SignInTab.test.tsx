import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { UserRecord } from "@/api/identity";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { SignInTab } from "./SignInTab";

afterEach(() => {
  vi.unstubAllGlobals();
});

const user: UserRecord = {
  id: "u1",
  login_id: "ada@contoso.test",
  display_name: "Ada Lovelace",
  type: "user",
  status: "active",
  email: "ada@contoso.test",
  home_unit: { id: "unit-1", name: "Acme", code: "acme" },
  last_login_at: null,
  created_at: "2026-10-01T09:00:00Z",
  updated_at: "2026-10-01T09:00:00Z",
  version: 1,
  sso_only: false,
  sso_only_by_provider: false,
  sso_links: [
    {
      provider_id: "p1",
      provider_name: "Contoso Entra ID",
      email: "ada@contoso.test",
      linked_at: "2026-10-22T09:00:00Z",
      last_sign_in_at: "2026-10-22T09:30:00Z",
    },
  ],
};

describe("how a person signs in, on their record", () => {
  it("shows their company logins, and lets an administrator who may manage them require one [TF-014 AC2, ADR-0048]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["identity.users.manage", "identity.users.read"])),
      "GET /api/v1/me/access?unit=unit-1": () => json({ scope: "unit:unit-1", permissions: ["identity.users.manage"] }),
      "PUT /api/v1/users/u1/sso-only": () => json({ ...user, sso_only: true }),
    });
    renderWithProviders(<SignInTab user={user} />);
    const person = userEvent.setup();

    const links = await screen.findByRole("table");
    expect(within(links).getByText("Contoso Entra ID")).toBeInTheDocument();
    expect(screen.getByText(/With a password, or with a company login/)).toBeInTheDocument();
    await person.click(await screen.findByRole("button", { name: "Require their company login" }));

    const put = calls.find((c) => c.key === "PUT /api/v1/users/u1/sso-only");
    expect(JSON.parse(put?.init?.body as string)).toEqual({ sso_only: true });
  });

  it("offers no change to someone who may only read the person, and says when a provider holds them to it [TF-014 AC2, ADR-0048]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["identity.users.read"])),
    });
    renderWithProviders(<SignInTab user={{ ...user, sso_only_by_provider: true }} />);

    expect(await screen.findByText(/which signs in everyone it links that way/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /company login|password again/ })).toBeNull();
  });
});
