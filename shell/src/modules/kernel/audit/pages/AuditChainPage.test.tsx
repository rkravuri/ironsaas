import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { setTimeZone } from "@/i18n/dates";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { AuditChainPage } from "./AuditChainPage";

afterEach(() => {
  vi.unstubAllGlobals();
  setTimeZone(undefined);
});

const hash = (digit: string) => digit.repeat(64);

const chain = (verification: unknown) => ({
  head_position: 1204,
  head_hash: hash("a"),
  head_sealed_at: "2026-10-23T08:59:55Z",
  unsealed: 3,
  archived_up_to: 800,
  pruned_up_to: 0,
  seals: [
    { first_position: 1201, last_position: 1204, last_hash: hash("a"), sealed_at: "2026-10-23T08:59:55Z" },
    { first_position: 1190, last_position: 1200, last_hash: hash("b"), sealed_at: "2026-10-23T08:59:50Z" },
  ],
  anchored: true,
  anchored_up_to: 1204,
  latest_verification: verification,
});

describe("the audit chain's status", () => {
  it("shows the last verification's pass, the chain's head and its seals, the times in the person's zone [TF-065 AC2, ADR-0050]", async () => {
    setTimeZone("Asia/Kolkata");
    stubFetch({
      "GET /api/v1/me": () => json(me(["audit.events.read"])),
      "GET /api/v1/audit-chain": () =>
        json(
          chain({
            result: "passed",
            full: false,
            from_position: 1,
            to_position: 1204,
            events_checked: 1204,
            problems: [],
            restore_position: null,
            restore_at: null,
            verified_at: "2026-10-23T02:00:00Z",
          }),
        ),
    });
    renderWithProviders(<AuditChainPage />, { route: "/audit/chain" });

    expect(await screen.findByText("Passed")).toBeInTheDocument();
    expect(screen.getByText("Daily check, Oct 23, 2026, 7:30 AM GMT+5:30")).toBeInTheDocument();
    expect(screen.getByText("1204 events checked, positions 1 to 1204.")).toBeInTheDocument();
    expect(screen.getByText("Up to position 1204, the whole chain")).toBeInTheDocument();
    const seals = screen.getByRole("table", { name: "The latest 2 seals, newest first" });
    expect(within(seals).getAllByRole("row")[1]).toHaveTextContent("1201 to 1204");
  });

  it("reports a modified and a deleted event by their identifiers, and the restore point after a restore [TF-061 AC2, TF-061 AC3, ADR-0050]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["audit.events.read"])),
      "GET /api/v1/audit-chain": () =>
        json(
          chain({
            result: "failed",
            full: true,
            from_position: 1,
            to_position: 1204,
            events_checked: 1203,
            problems: [
              {
                kind: "modified",
                position: 17,
                event_id: "01a14e12-0000-7000-8000-000000000017",
                message: "The content hash differs.",
              },
              { kind: "missing", position: 18, event_id: null, message: "No event at position 18." },
            ],
            restore_position: 1100,
            restore_at: "2026-10-22T23:00:00Z",
            verified_at: "2026-10-23T02:00:00Z",
          }),
        ),
    });
    renderWithProviders(<AuditChainPage />, { route: "/audit/chain" });

    expect(await screen.findByText("Failed")).toBeInTheDocument();
    const problems = screen.getByRole("table", { name: "2 problems found" });
    const rows = within(problems).getAllByRole("row").slice(1);
    expect(rows[0]).toHaveTextContent("17");
    expect(rows[0]).toHaveTextContent("The event was changed after it was sealed.");
    expect(rows[0]).toHaveTextContent("01a14e12-0000-7000-8000-000000000017");
    expect(rows[1]).toHaveTextContent("An event is missing at this position.");
    expect(screen.getByText("The database was restored")).toBeInTheDocument();
    expect(screen.getByText(/The chain is whole up to position 1100/)).toBeInTheDocument();
  });

  it("says when the chain has not been verified yet [ADR-0050]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["audit.events.read"])),
      "GET /api/v1/audit-chain": () => json(chain(null)),
    });
    renderWithProviders(<AuditChainPage />, { route: "/audit/chain" });

    expect(await screen.findByText("Not verified yet")).toBeInTheDocument();
  });
});
