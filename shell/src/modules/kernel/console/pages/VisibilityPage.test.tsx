import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fill, json, me, renderWithProviders, stubFetch } from "@/test/render";
import { VisibilityPage } from "./VisibilityPage";

afterEach(() => {
  vi.unstubAllGlobals();
});

const meta = {
  page_number: 1,
  page_size: 200,
  total_count: 2,
  total_pages: 1,
  has_previous: false,
  has_next: false,
  page_size_capped: false,
  max_page_size: 200,
};
const links = { self: "x", first: "x", previous: null, next: null, last: "x" };

const apac = {
  id: "0199a000-0000-7000-8000-000000000001",
  code: "APAC",
  name: "Asia Pacific",
  description: "Our region in Asia and the Pacific",
  status: "active",
  holders: 3,
  updated_at: "2026-10-29T09:00:00Z",
  version: 1,
};
const latam = {
  ...apac,
  id: "0199a000-0000-7000-8000-000000000002",
  code: "LATAM",
  name: "Latin America",
  description: null,
  status: "retired",
  holders: 1,
};
const tagsList = { data: [apac, latam], meta, links };

describe("the console's content visibility page", () => {
  it("lists the tags with their status in words and how many hold each, and shows who holds the one chosen [ADR-0057]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["visibility.tags.read"])),
      "GET /api/v1/visibility-tags?page_size=200": () => json(tagsList),
      [`GET /api/v1/visibility-tags/${apac.id}/holders?page_number=1&page_size=50&sort=-created_at`]: () =>
        json({
          data: [
            {
              principal_id: "p1",
              name: "Ada Lovelace",
              type: "user",
              home_unit_id: null,
              since: "2026-10-28T09:00:00Z",
            },
          ],
          meta: { ...meta, page_size: 50, total_count: 1 },
          links,
        }),
    });
    renderWithProviders(<VisibilityPage />, { route: "/console/visibility" });
    const user = userEvent.setup();

    const table = await screen.findByRole("table", { name: "The visibility tags of this organisation" });
    const rows = within(table).getAllByRole("row").slice(1);
    expect(rows[0]).toHaveTextContent("Asia Pacific");
    expect(rows[0]).toHaveTextContent("APAC");
    expect(rows[0]).toHaveTextContent("Active");
    expect(rows[0]).toHaveTextContent("3");
    expect(rows[1]).toHaveTextContent("Retired");
    // Reading is not managing: no button to define or retire a tag.
    expect(screen.queryByRole("button", { name: "New tag" })).not.toBeInTheDocument();
    expect(within(rows[0]!).queryByRole("button", { name: "Retire Asia Pacific" })).not.toBeInTheDocument();

    await user.click(screen.getByLabelText("Tag", { selector: "input[aria-haspopup='listbox']" }));
    await user.click(await screen.findByRole("option", { name: "Asia Pacific (APAC)", hidden: true }));
    await waitFor(() =>
      expect(calls.some((call) => call.key.startsWith(`GET /api/v1/visibility-tags/${apac.id}/holders`))).toBe(true),
    );
  });

  it("creates a tag from its code, kept in capitals, and retires one behind a confirmation that says nobody sees more [ADR-0057]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["visibility.tags.read", "visibility.tags.manage"])),
      "GET /api/v1/visibility-tags?page_size=200": () => json(tagsList),
      "POST /api/v1/visibility-tags": () => json({ ...apac, id: "new", code: "EMEA", name: "Europe", holders: 0 }, 201),
      [`POST /api/v1/visibility-tags/${apac.id}/retire`]: () => json({ ...apac, status: "retired" }),
    });
    renderWithProviders(<VisibilityPage />, { route: "/console/visibility" });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "New tag" }));
    const dialog = await screen.findByRole("dialog", { name: "New visibility tag" });
    await fill(user, within(dialog).getByLabelText(/^Code/), "emea");
    await fill(user, within(dialog).getByLabelText(/^Name/), "Europe");
    await user.click(within(dialog).getByRole("button", { name: "Create the tag" }));
    expect(await screen.findByText("Created the tag Europe (EMEA).")).toBeInTheDocument();
    const created = calls.find((call) => call.key === "POST /api/v1/visibility-tags");
    expect(JSON.parse(created?.init?.body as string)).toEqual({ code: "emea", name: "Europe", description: null });

    await user.click(screen.getByRole("button", { name: "Retire Asia Pacific" }));
    const confirm = await screen.findByRole("dialog", { name: "Retire Asia Pacific" });
    expect(confirm).toHaveTextContent("nobody sees more than before");
    await user.click(within(confirm).getByRole("button", { name: "Retire" }));
    expect(await screen.findByText("Retired the tag Asia Pacific.")).toBeInTheDocument();
    expect(calls.some((call) => call.key === `POST /api/v1/visibility-tags/${apac.id}/retire`)).toBe(true);
  });
});
