import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { RolesPage } from "./RolesPage";

const administrator = ["authz.roles.manage", "authz.roles.read"];
const emptyList = {
  data: [],
  meta: {
    page_number: 1,
    page_size: 50,
    total_count: 0,
    total_pages: 1,
    has_previous: false,
    has_next: false,
    page_size_capped: false,
    max_page_size: 200,
  },
  links: { self: "/api/v1/roles", first: "/api/v1/roles", previous: null, next: null, last: "/api/v1/roles" },
};
const context = (status: "enabled" | "read_only" | "disabled", importsKilled = false) => ({
  entitlements: {
    revision: 1,
    plan: "standard",
    confirmed_at: "2026-10-20T09:00:00Z",
    stale: false,
    modules: [],
    features: [
      { code: "authorization.role_transfer", status, reason: status === "disabled" ? "not_licensed" : "licensed" },
    ],
  },
  flags: {
    known: true,
    confirmed_at: "2026-10-20T09:00:00Z",
    values: [
      {
        key: "kill.imports",
        type: "boolean",
        value: importsKilled,
        reason: importsKilled ? "targeting_match" : "default",
      },
    ],
    kill_switches: importsKilled
      ? [
          {
            path: "imports",
            flag: "kill.imports",
            since: "2026-10-20T08:30:00Z",
            reason: "An import fault is being fixed.",
          },
        ]
      : [],
  },
});

function open(status: "enabled" | "read_only" | "disabled", permissions = administrator, importsKilled = false) {
  const stub = stubFetch({
    "GET /api/v1/me": () => json(me(permissions)),
    "GET /api/v1/me/context": () => json(context(status, importsKilled)),
    "GET /api/v1/roles?page_number=1&page_size=50&sort=code": () => json(emptyList),
  });
  renderWithProviders(<RolesPage />, { route: "/console/roles" });
  return stub;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the roles page and role transfer (TF-046 AC2, ADR-0034)", () => {
  it("offers Export and Import while the tenant's plan has role transfer", async () => {
    open("enabled");

    expect(await screen.findByRole("button", { name: "Export roles" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Import roles" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /New role/ })).toBeInTheDocument();
  });

  it("hides both when the plan has no role transfer, and keeps the rest of the page", async () => {
    const { calls } = open("disabled");

    expect(await screen.findByRole("link", { name: /New role/ })).toBeInTheDocument();
    // The context has answered, and the answer keeps both away.
    await waitFor(() => expect(calls.some((call) => call.key === "GET /api/v1/me/context")).toBe(true));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByRole("button", { name: "Export roles" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Import roles" })).not.toBeInTheDocument();
  });

  it("keeps Export and hides Import when role transfer is read only", async () => {
    open("read_only");

    expect(await screen.findByRole("button", { name: "Export roles" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Import roles" })).not.toBeInTheDocument();
  });
});

describe("the roles page and the import kill switch (TF-055, ADR-0035)", () => {
  it("hides Import while kill.imports is on, and keeps Export and New role", async () => {
    open("enabled", administrator, true);

    expect(await screen.findByRole("button", { name: "Export roles" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /New role/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Import roles" })).not.toBeInTheDocument();
  });
});
