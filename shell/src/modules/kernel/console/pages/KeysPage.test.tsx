import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { KeysPage } from "./KeysPage";

const keys = {
  keys: [
    {
      key: "data",
      version: 2,
      minimum_version: 1,
      rotated_at: "2026-10-28T09:00:00Z",
      next_rotation_at: "2027-10-28T09:00:00Z",
      rotation_days: 365,
      reencryption: {
        rotation_id: "01a1aaaa-0000-7000-8000-0000000000a1",
        to_version: 2,
        done: 1200,
        total: 3000,
        started_at: "2026-10-28T09:00:05Z",
        finished_at: null,
      },
    },
    {
      key: "index",
      version: 3,
      minimum_version: 3,
      rotated_at: "2026-10-27T09:00:00Z",
      next_rotation_at: "2027-10-27T09:00:00Z",
      rotation_days: 365,
      reencryption: null,
    },
    {
      key: "signing",
      version: 1,
      minimum_version: 1,
      rotated_at: null,
      next_rotation_at: "2027-10-20T09:00:00Z",
      rotation_days: 365,
      reencryption: null,
    },
    {
      key: "database",
      version: 2,
      minimum_version: 2,
      rotated_at: "2026-10-26T09:00:00Z",
      next_rotation_at: "2027-01-24T09:00:00Z",
      rotation_days: 90,
      reencryption: null,
    },
  ],
  rotations: [
    {
      id: "01a1aaaa-0000-7000-8000-0000000000a1",
      key: "data",
      trigger: "operator",
      requested_by: "Olga Operator",
      reason: "A contractor with access left",
      from_version: 1,
      to_version: 2,
      rotated_at: "2026-10-28T09:00:00Z",
      done: 1200,
      total: 3000,
      finished_at: null,
      retired_at: null,
    },
    {
      id: "01a1aaaa-0000-7000-8000-0000000000a2",
      key: "index",
      trigger: "schedule",
      requested_by: "schedule",
      reason: "Scheduled rotation, every 365 days.",
      from_version: 2,
      to_version: 3,
      rotated_at: "2026-10-27T09:00:00Z",
      done: 3000,
      total: 3000,
      finished_at: "2026-10-27T09:02:00Z",
      retired_at: "2026-10-27T09:02:30Z",
    },
  ],
  asked_at: "2026-10-28T09:01:00Z",
};

function open(body: unknown) {
  stubFetch({
    "GET /api/v1/me": () => json(me(["keys.keys.read"])),
    "GET /api/v1/keys": () => json(body),
  });
  renderWithProviders(<KeysPage />, { route: "/console/keys" });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the keys page (TF-075, ADR-0054)", () => {
  it("shows each key's version, its last and next rotation, and the running re-encryption's progress in words [ADR-0054, ADR-0024]", async () => {
    open(keys);

    expect(await screen.findByRole("heading", { level: 2, name: "Keys" })).toBeInTheDocument();
    const table = screen.getByRole("table", { name: "The keys" });
    const data = within(table).getByRole("row", { name: /Data key/ });
    expect(within(data).getByText("Version 2")).toBeInTheDocument();
    expect(within(data).getByText("Versions from 1 still read")).toBeInTheDocument();
    expect(within(data).getByText("1,200 of 3,000 values moved to version 2")).toBeInTheDocument();
    expect(
      within(data).getByRole("progressbar", { name: "1,200 of 3,000 values moved to version 2" }),
    ).toBeInTheDocument();
    expect(within(data).getByText("Every 365 days")).toBeInTheDocument();
    const signing = within(table).getByRole("row", { name: /Signing key/ });
    expect(within(signing).getByText("Not rotated yet")).toBeInTheDocument();
    expect(within(signing).getByText("Nothing to move")).toBeInTheDocument();
    expect(within(table).getByRole("row", { name: /Database password/ })).toHaveTextContent("Every 90 days");

    // Read only: the platform operator rotates keys.
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("lists the latest rotations with who asked and why, and where each stands [ADR-0054]", async () => {
    open(keys);

    const rotations = await screen.findByRole("table", { name: "Latest rotations" });
    const operator = within(rotations).getByRole("row", { name: /Data key/ });
    expect(within(operator).getByText("Olga Operator")).toBeInTheDocument();
    expect(within(operator).getByText("A contractor with access left")).toBeInTheDocument();
    expect(within(operator).getByText("Moving values: 1,200 of 3,000")).toBeInTheDocument();
    const scheduled = within(rotations).getByRole("row", { name: /Index key/ });
    expect(within(scheduled).getByText("The schedule")).toBeInTheDocument();
    expect(within(scheduled).getByText("Old version retired")).toBeInTheDocument();
  });

  it("says when the keys' state has not arrived yet [ADR-0054]", async () => {
    open({ keys: [], rotations: [], asked_at: null });

    expect(await screen.findByText("No keys to show yet")).toBeInTheDocument();
    expect(screen.getByText("No rotation yet")).toBeInTheDocument();
  });
});
