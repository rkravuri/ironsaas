import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { AuditReviewRecordPage } from "./AuditReviewRecordPage";
import { AuditReviewsPage } from "./AuditReviewsPage";
import { SignedSnapshotPage } from "./SignedSnapshotPage";
import { setTimeZone } from "@/i18n/dates";
import { fill, json, me, renderWithProviders, stubFetch } from "@/test/render";

afterEach(() => {
  setTimeZone(undefined);
  vi.unstubAllGlobals();
  window.sessionStorage.clear();
});

const reviewer = me(["compliance.audit_reviews.read", "compliance.audit_reviews.record", "audit.events.read"]);
const emptyPage = {
  data: [],
  meta: {
    page_number: 1,
    page_size: 25,
    total_count: 0,
    total_pages: 0,
    has_previous: false,
    has_next: false,
    page_size_capped: false,
    max_page_size: 200,
  },
  links: { self: "x", first: "x", previous: null, next: null, last: "x" },
};
const signature = {
  id: "sig-9",
  record_type: "audit_trail_review",
  record_id: "rev-1",
  record_version: 1,
  signer_id: "u1",
  signer_name: "Rita Reviewer",
  signer_login_id: "rita@acme.localhost",
  method: "password",
  meaning: "reviewed",
  comment: null,
  signed_at: "2026-10-22T09:00:00Z",
  snapshot_hash: "ab".repeat(32),
  hash_matches: true,
  current_version: 1,
  record_changed: false,
};
const review = {
  id: "rev-1",
  period_from: "2026-09-01",
  period_to: "2026-09-30",
  outcome: "findings",
  reviewer_id: "u1",
  reviewer_name: "Rita Reviewer",
  recorded_at: "2026-10-22T09:00:00Z",
  unit_ids: [],
  event_types: ["identity.login_failed"],
  notes: "Two failed sign-ins from one address.",
  searches: [{ label: "Failed sign-ins in September", link: "/audit?event_type=identity.login_failed" }],
  signature,
};

describe("the audit-trail reviews page", () => {
  it("lists the last months, a month nobody reviewed in words, and the reviews under them [TF-219 AC3]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(reviewer),
      "GET /api/v1/audit-reviews/coverage?months=12": () =>
        json({
          months: [
            { from: "2026-10-01", to: "2026-10-31", reviews: [], outcomes: [] },
            { from: "2026-09-01", to: "2026-09-30", reviews: ["rev-1"], outcomes: ["findings"] },
          ],
        }),
      "GET /api/v1/audit-reviews?page_number=1&page_size=50&sort=-period_from": () => json(emptyPage),
    });
    renderWithProviders(<AuditReviewsPage />);

    const months = await screen.findByRole("region", { name: "The last twelve months" });
    expect(await within(months).findByText("Oct 2026: not reviewed")).toBeInTheDocument();
    expect(within(months).getByText("Sep 2026: reviewed")).toBeInTheDocument();
  });

  it("records a review in a form, then signs it reviewed, and sends both together [TF-219 AC3, TF-063 AC4, ADR-0051]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(reviewer),
      "GET /api/v1/audit-reviews/coverage?months=12": () => json({ months: [] }),
      "GET /api/v1/audit-reviews?page_number=1&page_size=50&sort=-period_from": () => json(emptyPage),
      "GET /api/v1/organisation-units": () => json({ data: [] }),
      "GET /api/v1/audit-events/types": () =>
        json({ data: [{ code: "identity.login_failed", name: "Sign-in failed" }] }),
      "GET /api/v1/signatures/challenge?record_type=audit_trail_review": () =>
        json({
          first_in_session: true,
          login_id: true,
          password: true,
          second_factor: false,
          methods: [],
          passkey_options: null,
          provider_id: null,
          provider_name: null,
          provider_confirmed: false,
          reauthenticate: true,
          policy: { required: true, meanings: ["reviewed"], reauthenticate: true },
        }),
      "POST /api/v1/audit-reviews": () => json(review, 201),
      "GET /api/v1/audit-reviews/rev-1": () => json(review),
    });
    renderWithProviders(
      <Routes>
        <Route path="/" element={<AuditReviewsPage />} />
        <Route path="/compliance/audit-reviews/:id" element={<p>The review</p>} />
      </Routes>,
    );
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Record a review" }));
    const form = await screen.findByRole("dialog", { name: "Record a review of the audit trail" });
    await fill(user, within(form).getByLabelText(/^from/i), "2026-09-01");
    await fill(user, within(form).getByLabelText(/^to/i), "2026-09-30");
    await user.click(within(form).getByRole("radio", { name: "Findings" }));
    await user.click(within(form).getByRole("button", { name: "Continue to sign" }));
    expect(await within(form).findByText("Say what you found: findings need notes.")).toBeInTheDocument();
    await fill(user, within(form).getByLabelText(/^notes/i), "Two failed sign-ins from one address.");
    await user.click(within(form).getByRole("button", { name: "Add a search" }));
    await fill(user, within(form).getByLabelText(/what it looked for/i), "Failed sign-ins in September");
    await fill(user, within(form).getByLabelText(/search 1: address/i), "/audit?event_type=identity.login_failed");
    await user.click(within(form).getByRole("button", { name: "Continue to sign" }));

    const signing = await screen.findByRole("dialog", { name: "Sign the review" });
    expect(signing).toHaveTextContent("Reviewed");
    await fill(user, await within(signing).findByLabelText(/^login identifier/i), "rita@acme.localhost");
    await fill(user, within(signing).getByLabelText(/^password/i), "correct horse battery staple");
    await user.click(within(signing).getByRole("button", { name: "Sign" }));

    expect(await screen.findByText("The review")).toBeInTheDocument();
    const sent = calls.find((call) => call.key === "POST /api/v1/audit-reviews");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({
      period_from: "2026-09-01",
      period_to: "2026-09-30",
      unit_ids: [],
      event_types: [],
      outcome: "findings",
      notes: "Two failed sign-ins from one address.",
      searches: [{ label: "Failed sign-ins in September", link: "/audit?event_type=identity.login_failed" }],
      signature: { login_id: "rita@acme.localhost", password: "correct horse battery staple" },
    });
  });

  it("shows a review with its period, reviewer and outcome, its signature block, and its searches as links [TF-219 AC1, TF-219 AC3]", async () => {
    setTimeZone("UTC");
    stubFetch({
      "GET /api/v1/me": () => json(reviewer),
      "GET /api/v1/audit-reviews/rev-1": () => json(review),
      "GET /api/v1/signatures?record_type=audit_trail_review&record_id=rev-1": () => json({ data: [signature] }),
      "GET /api/v1/audit-events/types": () =>
        json({ data: [{ code: "identity.login_failed", name: "Sign-in failed" }] }),
    });
    renderWithProviders(
      <Routes>
        <Route path="/compliance/audit-reviews/:id" element={<AuditReviewRecordPage />} />
      </Routes>,
      { route: "/compliance/audit-reviews/rev-1" },
    );

    expect(
      await screen.findByRole("heading", { name: "Audit-trail review, Sep 1, 2026 to Sep 30, 2026" }),
    ).toBeInTheDocument();
    expect(screen.getByText("Findings", { selector: ".mantine-Badge-label" })).toBeInTheDocument();
    const block = await screen.findByRole("region", { name: "Signatures" });
    expect(block).toHaveTextContent(/Rita Reviewer, Oct 22, 2026, 9:00\s?AM UTC, Reviewed/);
    expect(await screen.findByText("Sign-in failed")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Failed sign-ins in September" })).toHaveAttribute(
      "href",
      "/audit?event_type=identity.login_failed",
    );
  });
});

describe("the signed snapshot", () => {
  it("shows what was signed at its version, the hash kept and the hash recomputed now [TF-063 AC2, TF-063 AC3]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/signatures/sig-1/snapshot": () =>
        json({
          signature: {
            ...signature,
            id: "sig-1",
            record_type: "sample_document",
            record_changed: true,
            current_version: 2,
          },
          type_name: "Sample document",
          canonical: '["iron-snapshot-v1","sample_document","rev-1",1,{"body":"Ten valves a month."}]',
          form: 1,
          recomputed_hash: "ab".repeat(32),
          content: { body: "Ten valves a month.", bank_account: null },
        }),
    });
    renderWithProviders(
      <Routes>
        <Route path="/signatures/:id" element={<SignedSnapshotPage />} />
      </Routes>,
      { route: "/signatures/sig-1" },
    );

    expect(await screen.findByRole("heading", { name: "What was signed: Sample document" })).toBeInTheDocument();
    expect(screen.getByText("Hash matches")).toBeInTheDocument();
    const content = screen.getByRole("table");
    expect(content).toHaveTextContent(/BodyTen valves a month\./);
    expect(content).toHaveTextContent(/Bank accountEmpty/);
    await userEvent.setup().click(screen.getByRole("tab", { name: "Canonical form" }));
    await waitFor(() => expect(screen.getAllByText("ab".repeat(32))).toHaveLength(2));
  });
  it("reopens the review's signature when the reviewer comes back from their company login, ready to confirm [TF-063 AC5, TF-219 AC3, ADR-0051]", async () => {
    const input = {
      period_from: "2026-09-01",
      period_to: "2026-09-30",
      unit_ids: [],
      event_types: [],
      outcome: "no_findings",
      notes: null,
      searches: [],
    };
    window.sessionStorage.setItem(
      "iron.signing.pending",
      JSON.stringify({
        path: "/",
        recordType: "audit_trail_review",
        meaning: "reviewed",
        comment: "",
        context: input,
        savedAt: Date.now(),
      }),
    );
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(reviewer),
      "GET /api/v1/audit-reviews/coverage?months=12": () => json({ months: [] }),
      "GET /api/v1/audit-reviews?page_number=1&page_size=50&sort=-period_from": () => json(emptyPage),
      "GET /api/v1/signatures/challenge?record_type=audit_trail_review": () =>
        json({
          first_in_session: false,
          login_id: false,
          password: false,
          second_factor: false,
          methods: [],
          passkey_options: null,
          provider_id: "p1",
          provider_name: "Acme Login",
          provider_confirmed: true,
          reauthenticate: true,
          policy: { required: true, meanings: ["reviewed"], reauthenticate: true },
        }),
      "POST /api/v1/audit-reviews": () => json(review, 201),
    });
    renderWithProviders(
      <Routes>
        <Route path="/" element={<AuditReviewsPage />} />
        <Route path="/compliance/audit-reviews/:id" element={<p>The review</p>} />
      </Routes>,
    );
    const user = userEvent.setup();

    const signing = await screen.findByRole("dialog", { name: "Sign the review" });
    expect(signing).toHaveTextContent("You sign the review of Sep 1, 2026 to Sep 30, 2026");
    expect(await within(signing).findByText("Acme Login confirmed it is you. Sign now.")).toBeInTheDocument();
    await user.click(within(signing).getByRole("button", { name: "Sign" }));

    expect(await screen.findByText("The review")).toBeInTheDocument();
    const sent = calls.find((call) => call.key === "POST /api/v1/audit-reviews");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({ ...input, signature: {} });
    expect(window.sessionStorage.getItem("iron.signing.pending")).toBeNull();
  });
});
