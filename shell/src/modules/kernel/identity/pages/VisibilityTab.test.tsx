import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { UserRecord } from "@/api/identity";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { VisibilityTab } from "./VisibilityTab";

afterEach(() => {
  vi.unstubAllGlobals();
});

const user = { id: "u1", display_name: "Grace Hopper" } as UserRecord;

describe("a person's Visibility tab", () => {
  it("says a person without tags sees every record their roles allow, and gives them only the tags the administrator may give [ADR-0057]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["visibility.tags.read"])),
      "GET /api/v1/users/u1/visibility-tags": () =>
        json({
          principal_id: "u1",
          tags: [],
          may_change: true,
          choices: [{ code: "APAC", name: "Asia Pacific", active: true }],
        }),
      "PUT /api/v1/users/u1/visibility-tags": () =>
        json({
          principal_id: "u1",
          tags: [{ code: "APAC", name: "Asia Pacific", active: true }],
          may_change: true,
          choices: [],
        }),
    });
    renderWithProviders(<VisibilityTab user={user} />);
    const person = userEvent.setup();

    expect(
      await screen.findByText("No visibility tags: Grace Hopper sees every record their roles and units allow."),
    ).toBeInTheDocument();
    await person.click(screen.getByRole("button", { name: "Change tags" }));
    const dialog = await screen.findByRole("dialog", { name: "Change the visibility tags of Grace Hopper" });
    expect(within(dialog).getAllByRole("checkbox")).toHaveLength(1);
    await person.click(within(dialog).getByRole("checkbox", { name: "Asia Pacific (APAC)" }));
    await person.click(within(dialog).getByRole("button", { name: "Save the tags" }));

    expect(await screen.findByRole("list", { name: "Visibility tags" })).toHaveTextContent("Asia Pacific (APAC)");
    expect(
      JSON.parse(calls.find((call) => call.key === "PUT /api/v1/users/u1/visibility-tags")?.init?.body as string),
    ).toEqual({ codes: ["APAC"] });
  });

  it("shows the tags without a way to change them to a reader who may not [ADR-0057]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["visibility.tags.read"])),
      "GET /api/v1/users/u1/visibility-tags": () =>
        json({
          principal_id: "u1",
          tags: [{ code: "APAC", name: "Asia Pacific", active: true }],
          may_change: false,
          choices: [],
        }),
    });
    renderWithProviders(<VisibilityTab user={user} />);

    expect(await screen.findByRole("list", { name: "Visibility tags" })).toHaveTextContent("Asia Pacific (APAC)");
    expect(screen.queryByRole("button", { name: "Change tags" })).not.toBeInTheDocument();
  });
});
