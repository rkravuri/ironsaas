import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setTimeZone } from "@/i18n/dates";
import { fill, json, me, renderWithProviders, stubFetch } from "@/test/render";
import { LegalHoldsPage } from "./LegalHoldsPage";

afterEach(() => {
  vi.unstubAllGlobals();
  setTimeZone(undefined);
});

const supplier = "01a14e12-0000-7000-8000-0000000000a1";
const standing = {
  id: "01a14e12-0000-7000-8000-0000000000b1",
  scope: "master",
  record_type: "supplier",
  record_id: supplier,
  reason: "Regulator's inquiry 2026-14",
  placed_at: "2026-10-20T09:00:00Z",
  placed_by: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
  placed_by_name: "Ada Admin",
  released_at: null,
  release_reason: null,
};

describe("legal holds", () => {
  it("lists a standing hold with what it keeps, why, who placed it and when in the person's zone [TF-065 AC2, ADR-0050]", async () => {
    setTimeZone("Europe/Berlin");
    stubFetch({
      "GET /api/v1/me": () => json(me(["audit.events.read"])),
      "GET /api/v1/legal-holds": () => json({ holds: [standing] }),
    });
    renderWithProviders(<LegalHoldsPage />, { route: "/audit/legal-holds" });

    const table = await screen.findByRole("table", { name: "1 standing hold" });
    const row = within(table).getAllByRole("row")[1]!;
    expect(row).toHaveTextContent("A master's records");
    expect(row).toHaveTextContent(`Every record that belongs to the supplier ${supplier}.`);
    expect(row).toHaveTextContent("Regulator's inquiry 2026-14");
    expect(row).toHaveTextContent("Oct 20, 2026, 11:00 AM GMT+2");
    expect(row).toHaveTextContent("Ada Admin");
    // Reading holds is not placing them: no button without the critical permission.
    expect(screen.queryByRole("button", { name: "Place a legal hold" })).toBeNull();
  });

  it("places a hold on a record with a reason, and refuses one without a reason or a record [ADR-0050]", async () => {
    let placed = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["audit.events.read", "audit.legal_holds.manage"])),
      "GET /api/v1/legal-holds": () => json({ holds: placed ? [standing] : [] }),
      "POST /api/v1/legal-holds": () => {
        placed = true;
        return json(standing, 201);
      },
    });
    renderWithProviders(<LegalHoldsPage />, { route: "/audit/legal-holds" });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Place a legal hold" }));
    const dialog = await screen.findByRole("dialog", { name: "Place a legal hold" });
    await user.click(within(dialog).getByRole("button", { name: "Place the hold" }));
    expect(await within(dialog).findByText("Say which kind of record it is.")).toBeInTheDocument();
    expect(within(dialog).getByText("Say why: the reason is kept in the audit trail.")).toBeInTheDocument();
    expect(calls.some((call) => call.key === "POST /api/v1/legal-holds")).toBe(false);

    await fill(user, within(dialog).getByLabelText(/^Record type/), "supplier");
    await fill(user, within(dialog).getByLabelText(/^Record identifier/), supplier);
    await fill(user, within(dialog).getByLabelText(/^Why it is held/), "Regulator's inquiry 2026-14");
    await user.click(within(dialog).getByRole("button", { name: "Place the hold" }));

    expect(await screen.findByText(/The hold stands/)).toBeInTheDocument();
    const sent = calls.find((call) => call.key === "POST /api/v1/legal-holds");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({
      scope: "record",
      record_type: "supplier",
      record_id: supplier,
      reason: "Regulator's inquiry 2026-14",
    });
  });

  it("releases a hold with a reason, and it stays listed as released [ADR-0050]", async () => {
    let released = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["audit.events.read", "audit.legal_holds.manage"])),
      "GET /api/v1/legal-holds": () =>
        json({
          holds: [
            released
              ? { ...standing, released_at: "2026-10-23T09:00:00Z", release_reason: "Inquiry closed" }
              : standing,
          ],
        }),
      [`POST /api/v1/legal-holds/${standing.id}/release`]: () => {
        released = true;
        return json({ ...standing, released_at: "2026-10-23T09:00:00Z", release_reason: "Inquiry closed" });
      },
    });
    renderWithProviders(<LegalHoldsPage />, { route: "/audit/legal-holds" });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: /^Release the hold on/ }));
    const dialog = await screen.findByRole("dialog", { name: "Release this legal hold?" });
    await fill(user, within(dialog).getByLabelText(/^Why it is released/), "Inquiry closed");
    await user.click(within(dialog).getByRole("button", { name: "Release" }));

    expect(await screen.findByRole("table", { name: "1 released hold" })).toHaveTextContent("Inquiry closed");
    await waitFor(() => expect(screen.queryByRole("table", { name: "1 standing hold" })).toBeNull());
    const sent = calls.find((call) => call.key === `POST /api/v1/legal-holds/${standing.id}/release`);
    expect(JSON.parse(sent?.init?.body as string)).toEqual({ reason: "Inquiry closed" });
  });
});
