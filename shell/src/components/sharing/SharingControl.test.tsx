import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { SharingControl } from "./SharingControl";

const unit = (id: string, name: string, parent: string | null, depth: number) => ({ id, name, code: name.toLowerCase(), parent_id: parent, depth });
const choices = [unit("acme", "Acme", null, 0), unit("sales", "Sales", "acme", 1), unit("north", "North", "sales", 2), unit("finance", "Finance", "acme", 1)];
const named = ({ id, name, code }: { id: string; name: string; code: string }) => ({ id, name, code });
const path = "/api/v1/record-sharing/sales_order/o1";

const sharing = (mode: "private" | "shared" | "tenant", { units = [] as string[], mayChange = true } = {}) => ({
  record_type: "sales_order",
  record_id: "o1",
  owning_unit: named(choices[2]!),
  mode,
  units: choices.filter((choice) => units.includes(choice.id)).map(named),
  may_change: mayChange,
  choices: mayChange ? choices : [],
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the sharing control on a record page (TF-222, ADR-0038)", () => {
  it("says who sees the record in words for each mode", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me()), [`GET ${path}`]: () => json(sharing("shared", { units: ["finance", "sales"], mayChange: false })) });
    renderWithProviders(<SharingControl recordType="sales_order" recordId="o1" />);

    expect(await screen.findByText("Belongs to North and is shared with Sales and Finance: the people whose roles cover any of them see it.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Change who sees this" })).toBeNull();
  });

  it("says a private record is its unit's, and a tenant one the whole organisation's", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me()), [`GET ${path}`]: () => json(sharing("private")), "GET /api/v1/record-sharing/sales_order/o2": () => json({ ...sharing("tenant"), record_id: "o2" }) });
    renderWithProviders(
      <>
        <SharingControl recordType="sales_order" recordId="o1" />
        <SharingControl recordType="sales_order" recordId="o2" />
      </>,
    );

    expect(await screen.findByText("Private to North: the people whose roles cover North see it.")).toBeInTheDocument();
    expect(await screen.findByText("Belongs to North and is open to the whole organisation: everyone who may see these records sees it.")).toBeInTheDocument();
  });

  it("shares with units chosen on the tree, a unit never choosing those below it, and asks for one at least", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      [`GET ${path}`]: () => json(sharing("private")),
      [`PUT ${path}`]: () => json(sharing("shared", { units: ["sales"] })),
    });
    renderWithProviders(<SharingControl recordType="sales_order" recordId="o1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Change who sees this" }));
    const dialog = await screen.findByRole("dialog", { name: "Who sees this record" });
    fireEvent.click(within(dialog).getByRole("radio", { name: "Shared with chosen units" }));
    const group = within(dialog).getByRole("group", { name: "Share with" });
    // The record's own unit sees it whatever the mode, so it is not offered; a child names its parent.
    expect(within(group).queryByRole("checkbox", { name: /^North/ })).toBeNull();
    expect(within(group).getByRole("checkbox", { name: "Sales, under Acme" })).not.toBeChecked();
    fireEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    expect(await within(dialog).findByText("Choose at least one unit to share with.")).toBeInTheDocument();
    expect(calls.some((call) => call.key === `PUT ${path}`)).toBe(false);

    fireEvent.click(within(group).getByRole("checkbox", { name: "Sales, under Acme" }));
    fireEvent.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(calls.some((call) => call.key === `PUT ${path}`)).toBe(true));
    const put = calls.find((call) => call.key === `PUT ${path}`);
    expect(JSON.parse(put?.init?.body as string)).toEqual({ mode: "shared", units: ["sales"] });
    expect(await screen.findByText("Belongs to North and is shared with Sales: the people whose roles cover any of them see it.")).toBeInTheDocument();
  });

  it("opens to the whole organisation with no units listed", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      [`GET ${path}`]: () => json(sharing("shared", { units: ["finance"] })),
      [`PUT ${path}`]: () => json(sharing("tenant")),
    });
    renderWithProviders(<SharingControl recordType="sales_order" recordId="o1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Change who sees this" }));
    const dialog = await screen.findByRole("dialog", { name: "Who sees this record" });
    fireEvent.click(within(dialog).getByRole("radio", { name: "The whole organisation" }));
    expect(within(dialog).queryByRole("group", { name: "Share with" })).toBeNull();
    fireEvent.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(calls.some((call) => call.key === `PUT ${path}`)).toBe(true));
    expect(JSON.parse(calls.find((call) => call.key === `PUT ${path}`)?.init?.body as string)).toEqual({ mode: "tenant", units: [] });
  });
});
