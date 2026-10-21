import { fireEvent, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { ImportRolesDialog } from "./ImportRolesDialog";

const exported = {
  format_version: 1,
  roles: [
    { code: "buyer", name: "Buyer", description: "", principal_type: "user", permissions: ["identity.users.read"] },
  ],
};

const diff = (extra: Record<string, unknown> = {}) => ({
  dry_run: true,
  applied: false,
  created: ["buyer"],
  changed: [{ code: "planner", added: ["authz.units.manage"], removed: ["authz.roles.read"], fields: ["name"] }],
  unchanged: ["auditor"],
  unknown: [],
  skipped_system: ["tenant-administrator"],
  ...extra,
});

const paste = async () => {
  const dialog = await screen.findByRole("dialog", { name: "Import roles" });
  fireEvent.change(within(dialog).getByLabelText("Or paste the export"), {
    target: { value: JSON.stringify(exported) },
  });
  fireEvent.click(within(dialog).getByRole("button", { name: "Check the import" }));
  return dialog;
};

describe("the role import", () => {
  it("shows the dry run's diff first and applies only when asked [ADR-0031]", async () => {
    const onApplied = vi.fn();
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["authz.roles.manage"])),
      "POST /api/v1/roles/import": (init) =>
        json(
          (JSON.parse(init?.body as string) as { dry_run: boolean }).dry_run
            ? diff()
            : diff({ dry_run: false, applied: true }),
        ),
    });
    renderWithProviders(<ImportRolesDialog onClose={() => undefined} onApplied={onApplied} />);

    const dialog = await paste();

    expect(await within(dialog).findByText("1 new role")).toBeInTheDocument();
    expect(dialog).toHaveTextContent("Gains: authz.units.manage");
    expect(dialog).toHaveTextContent("Loses: authz.roles.read");
    expect(dialog).toHaveTextContent("Also changes: the name");
    expect(dialog).toHaveTextContent("1 role stays as it is");
    expect(dialog).toHaveTextContent("1 system role skipped");
    expect(calls.filter((call) => call.key === "POST /api/v1/roles/import")).toHaveLength(1);

    fireEvent.click(within(dialog).getByRole("button", { name: "Apply the import" }));

    await vi.waitFor(() => expect(onApplied).toHaveBeenCalled());
    const bodies = calls
      .filter((call) => call.key === "POST /api/v1/roles/import")
      .map((call) => JSON.parse(call.init?.body as string) as { dry_run: boolean; document: unknown });
    expect(bodies.map((body) => body.dry_run)).toEqual([true, false]);
    expect(bodies[1]?.document).toEqual(exported);
  });

  it("names the unknown permissions and does not let the import apply [TF-032 AC3]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["authz.roles.manage"])),
      "POST /api/v1/roles/import": () =>
        json(diff({ unknown: [{ code: "buyer", permissions: ["procurement.orders.create"] }] })),
    });
    renderWithProviders(<ImportRolesDialog onClose={() => undefined} onApplied={() => undefined} />);

    const dialog = await paste();

    expect(await within(dialog).findByText("Permissions this tenant does not know")).toBeInTheDocument();
    expect(dialog).toHaveTextContent("procurement.orders.create");
    expect(within(dialog).getByRole("button", { name: "Apply the import" })).toBeDisabled();
  });

  it("says so when the text is not an export, without calling the API [ADR-0027]", async () => {
    const { calls } = stubFetch({ "GET /api/v1/me": () => json(me(["authz.roles.manage"])) });
    renderWithProviders(<ImportRolesDialog onClose={() => undefined} onApplied={() => undefined} />);

    const dialog = await screen.findByRole("dialog", { name: "Import roles" });
    fireEvent.change(within(dialog).getByLabelText("Or paste the export"), { target: { value: "not json" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Check the import" }));

    expect(await within(dialog).findByRole("alert")).toHaveTextContent("This is not a role export.");
    expect(calls.some((call) => call.key.startsWith("POST"))).toBe(false);
  });
});
