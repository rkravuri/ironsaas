import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { EvidencePackRecordPage } from "./EvidencePackRecordPage";
import { EvidencePacksPage } from "./EvidencePacksPage";
import { ValidationPage } from "./ValidationPage";
import { setTimeZone } from "@/i18n/dates";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";

afterEach(() => {
  setTimeZone(undefined);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const exporter = me(["compliance.evidence.export"]);
const emptyPage = {
  data: [],
  meta: {
    page_number: 1,
    page_size: 50,
    total_count: 0,
    total_pages: 0,
    has_previous: false,
    has_next: false,
    page_size_capped: false,
    max_page_size: 200,
  },
  links: { self: "x", first: "x", previous: null, next: null, last: "x" },
};
const pack = {
  id: "pack-1",
  period_from: "2026-09-01T00:00:00Z",
  period_to: "2026-10-01T00:00:00Z",
  requested_by: exporter.id,
  requester_name: "Admin",
  requested_at: "2026-10-23T09:00:00Z",
  status: "ready",
  bytes: 84_000,
  sha256: "a1".repeat(32),
  manifest_sha256: "b2".repeat(32),
  chain_segment: 3,
  chain_position: 1204,
  chain_hash: "c3".repeat(32),
  anchor_id: "anchor-77",
  made_at: "2026-10-23T09:00:04Z",
  failure: null,
};

describe("the evidence packs", () => {
  it("asks for a pack for a period of whole UTC days and opens it [TF-067 AC1]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(exporter),
      "GET /api/v1/evidence-packs?page_number=1&page_size=50": () => json(emptyPage),
      "POST /api/v1/evidence-packs": () => json({ ...pack, status: "requested", bytes: null }, 202),
    });
    renderWithProviders(
      <Routes>
        <Route path="/" element={<EvidencePacksPage />} />
        <Route path="/compliance/evidence-packs/:id" element={<p>The pack</p>} />
      </Routes>,
    );
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Make a pack" }));
    const form = await screen.findByRole("dialog", { name: "Make an evidence pack" });
    await fill(user, within(form).getByLabelText(/^from/i), "2026-09-01");
    await fill(user, within(form).getByLabelText(/^up to and including/i), "2026-09-30");
    await user.click(within(form).getByRole("button", { name: "Make the pack" }));

    expect(await screen.findByText("The pack")).toBeInTheDocument();
    const sent = calls.find((call) => call.key === "POST /api/v1/evidence-packs");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({
      from: "2026-09-01T00:00:00Z",
      to: "2026-10-01T00:00:00.000Z",
    });
  });

  it("shows a ready pack's hashes and the chain head with its anchor, and downloads it through a link of its own [TF-067 AC1, TF-067 AC2, ADR-0052]", async () => {
    setTimeZone("UTC");
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(exporter),
      "GET /api/v1/evidence-packs/pack-1": () => json(pack),
      "POST /api/v1/evidence-packs/pack-1/links": () =>
        json({ url: "/api/v1/evidence-packs/downloads/token-1", expires_at: "2026-10-23T09:05:00Z" }, 201),
    });
    const clicked: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      clicked.push(this.getAttribute("href") ?? "");
    });
    renderWithProviders(
      <Routes>
        <Route path="/compliance/evidence-packs/:id" element={<EvidencePackRecordPage />} />
      </Routes>,
      { route: "/compliance/evidence-packs/pack-1" },
    );
    const user = userEvent.setup();

    expect(
      await screen.findByRole("heading", { name: /Sep 1, 2026, 12:00\s?AM UTC to Oct 1, 2026, 12:00\s?AM UTC/ }),
    ).toBeInTheDocument();
    expect(screen.getByText("Ready", { selector: ".mantine-Badge-label" })).toBeInTheDocument();
    const facts = screen.getByRole("table");
    expect(facts).toHaveTextContent("a1".repeat(32));
    expect(facts).toHaveTextContent("b2".repeat(32));
    expect(facts).toHaveTextContent(`Segment 3, position 1204: ${"c3".repeat(32)}`);
    expect(facts).toHaveTextContent("anchor-77");

    await user.click(screen.getByRole("button", { name: "Download" }));
    await vi.waitFor(() => expect(clicked).toEqual(["/api/v1/evidence-packs/downloads/token-1"]));
    expect(calls.filter((call) => call.key === "POST /api/v1/evidence-packs/pack-1/links")).toHaveLength(1);
  });

  it("says a pack is being made, and shows the refusal of a person without the permission [TF-067 AC3]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(exporter),
      "GET /api/v1/evidence-packs/pack-1": () => json({ ...pack, status: "building", bytes: null, sha256: null }),
      "GET /api/v1/evidence-packs/pack-2": () =>
        problem(403, { detail: "You need the permission compliance.evidence.export.", error_code: "auth.forbidden" }),
    });
    const first = renderWithProviders(
      <Routes>
        <Route path="/compliance/evidence-packs/:id" element={<EvidencePackRecordPage />} />
      </Routes>,
      { route: "/compliance/evidence-packs/pack-1" },
    );
    expect(await screen.findByText("The pack is being made. This page updates when it is ready.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Download" })).not.toBeInTheDocument();
    first.unmount();

    renderWithProviders(
      <Routes>
        <Route path="/compliance/evidence-packs/:id" element={<EvidencePackRecordPage />} />
      </Routes>,
      { route: "/compliance/evidence-packs/pack-2" },
    );
    expect(await screen.findByRole("alert")).toHaveTextContent("You need the permission compliance.evidence.export.");
  });
});

describe("the validation page", () => {
  it("shows the installed version and downloads the pack of exactly that version [TF-218 AC3]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["compliance.validation.read"])),
      "GET /api/v1/validation": () =>
        json({ version: "2026.10.23.57", development: false, pack_available: true, pack_bytes: 2_400_000 }),
    });
    renderWithProviders(<ValidationPage />);

    expect(await screen.findByText("2026.10.23.57")).toBeInTheDocument();
    expect(screen.getByText(/The pack of version 2026.10.23.57, 2.3 MB/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Download the pack" })).toHaveAttribute("href", "/api/v1/validation/pack");
    expect(screen.queryByText(/development build/)).not.toBeInTheDocument();
  });

  it("says a laptop runs a development build, and that a missing pack is placed at the next update [TF-218 AC3, ADR-0052]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["compliance.validation.read"])),
      "GET /api/v1/validation": () =>
        json({ version: "development", development: true, pack_available: false, pack_bytes: null }),
    });
    renderWithProviders(<ValidationPage />);

    expect(await screen.findByText(/A development build: it has no release version/)).toBeInTheDocument();
    expect(screen.getByText(/The pack of this version is not here yet/)).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Download the pack" })).not.toBeInTheDocument();
  });
});
