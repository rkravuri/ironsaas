import { afterEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { DelegationsPage } from "./DelegationsPage";

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
  links: { self: "x", first: "x", previous: null, next: null, last: "x" },
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the console's delegations page, for administrators (TF-037, ADR-0037)", () => {
  it("lists others' delegations and offers to delegate for someone, with nothing chosen to end yet", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["authz.delegations.manage"])),
      "GET /api/v1/delegations?page_number=1&page_size=50&sort=-starts_at": () => json(emptyList),
    });
    renderWithProviders(<DelegationsPage />, { route: "/console/delegations" });

    expect(await screen.findByRole("heading", { name: "Delegations" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Delegate for someone" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "End the chosen delegation" })).toBeDisabled();
    // The person's own delegations are in My profile, not here.
    expect(calls.some((call) => call.key === "GET /api/v1/delegations/mine")).toBe(false);
  });
});
