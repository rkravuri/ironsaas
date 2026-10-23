import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { SignatureBlock } from "./SignatureBlock";
import { HistoryPanel } from "./HistoryPanel";
import { RecordPage } from "../templates/RecordPage";
import { setTimeZone } from "@/i18n/dates";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import type { Signature } from "@/api/compliance";

const signature = (overrides: Partial<Signature> = {}): Signature => ({
  id: "sig-1",
  record_type: "sample_document",
  record_id: "doc-1",
  record_version: 1,
  signer_id: "u1",
  signer_name: "Ada Lovelace",
  signer_login_id: "ada@acme.localhost",
  method: "password_and_app",
  meaning: "approved",
  comment: "Agreed with finance.",
  signed_at: "2026-10-22T09:00:00Z",
  snapshot_hash: "ab".repeat(32),
  hash_matches: true,
  current_version: 1,
  record_changed: false,
  ...overrides,
});

beforeEach(() => {
  setTimeZone("Asia/Kolkata");
});

afterEach(() => {
  setTimeZone(undefined);
  vi.unstubAllGlobals();
});

describe("the signature block", () => {
  it("shows each signer's full name, the date and time with the zone named, and the meaning [TF-219 AC1, TF-063 AC2]", () => {
    renderWithProviders(<SignatureBlock signatures={[signature()]} />);

    const block = screen.getByRole("region", { name: "Signatures" });
    const line = within(block).getByRole("listitem");
    expect(line).toHaveTextContent("Ada Lovelace");
    expect(line).toHaveTextContent(/Oct 22, 2026, 2:30\s?PM GMT\+5:30/);
    expect(line).toHaveTextContent("Approved");
    expect(line).toHaveTextContent(
      "Signs version 1, as ada@acme.localhost, with the password and an authenticator app.",
    );
    expect(line).toHaveTextContent("The signed content's hash matches the snapshot kept with the signature.");
    expect(within(block).queryByRole("link")).toBeNull();
  });

  it("says a changed record's signature applies to the earlier version, and links to what was signed [TF-063 AC3]", () => {
    renderWithProviders(<SignatureBlock signatures={[signature({ record_changed: true, current_version: 2 })]} />);

    expect(
      screen.getByText(
        /The record has changed since this signature: it applies to version 1, and the record is now at version 2\./,
      ),
    ).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "See what was signed" })).toHaveAttribute("href", "/signatures/sig-1");
  });

  it("names a hash that does not match in words, not by a colour [TF-063 AC2, ADR-0024]", () => {
    renderWithProviders(<SignatureBlock signatures={[signature({ hash_matches: false })]} />);

    expect(screen.getByText(/hash does not match the snapshot kept with it/)).toBeInTheDocument();
  });

  it("puts the block under a record's header with a way to print it, and leaves the tab list off paper [TF-219 AC1, ADR-0051]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/signatures?record_type=sample_document&record_id=doc-1": () => json({ data: [signature()] }),
    });
    const print = vi.fn();
    vi.stubGlobal("print", print);
    renderWithProviders(
      <RecordPage
        header={{ title: "Supply agreement" }}
        tabs={[{ value: "details", label: "Details", panel: <p>Ten valves a month.</p> }]}
        signatures={{ recordType: "sample_document", recordId: "doc-1" }}
      />,
    );

    expect(await screen.findByRole("region", { name: "Signatures" })).toHaveTextContent("Ada Lovelace");
    screen.getByRole("button", { name: "Print" }).click();
    expect(print).toHaveBeenCalled();
    expect(screen.getByRole("tablist").closest("[data-print='hide']")).not.toBeNull();
  });
});

describe("the History panel", () => {
  it("shows a save as one group of its fields, old and new side by side [TF-062 AC1]", () => {
    renderWithProviders(
      <HistoryPanel
        events={[
          {
            id: "e1",
            at: "2026-10-22T09:00:00Z",
            actor: "Ada Lovelace",
            action: "Record changed",
            changes: [
              { field: "body", old: "Ten valves a month.", new: "Twelve valves a month.", treatment: "plain" },
              { field: "amount", old: "1200.50", new: "1440.60", treatment: "plain" },
            ],
          },
        ]}
      />,
    );

    const group = screen.getByRole("table", { name: "2 fields changed" });
    const rows = within(group).getAllByRole("row");
    expect(rows).toHaveLength(3);
    expect(rows[1]).toHaveTextContent(/BodyTen valves a month\.Twelve valves a month\./);
    expect(rows[2]).toHaveTextContent(/Amount1200\.501440\.60/);
    expect(screen.getByText("Record changed")).toBeInTheDocument();
  });

  it("keeps a sensitive field masked and a secret only as changed [TF-062 AC3]", () => {
    renderWithProviders(
      <HistoryPanel
        events={[
          {
            id: "e1",
            at: "2026-10-22T09:00:00Z",
            actor: "Ada Lovelace",
            action: "Record changed",
            changes: [
              { field: "bank_account", old: "****5555", new: "****3000", treatment: "masked" },
              { field: "portal_key_hash", old: null, new: null, treatment: "secret" },
            ],
          },
        ]}
      />,
    );

    const rows = within(screen.getByRole("table", { name: "2 fields changed" })).getAllByRole("row");
    expect(rows[1]).toHaveTextContent("****5555");
    expect(rows[1]).toHaveTextContent("Kept masked: only its last characters are stored.");
    expect(rows[2]).toHaveTextContent("Portal key hash");
    expect(rows[2]).toHaveTextContent("Changed. A secret value is never kept.");
  });
});
