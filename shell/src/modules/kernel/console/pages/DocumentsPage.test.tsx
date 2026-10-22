import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fill, json, me, renderWithProviders, stubFetch } from "@/test/render";
import { DocumentsPage } from "./DocumentsPage";

afterEach(() => {
  vi.unstubAllGlobals();
});

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

const documents = {
  documents: [
    {
      source: "platform",
      key: "terms_of_service",
      version: 3,
      title: "Terms of service",
      body: "Terms.",
      published_at: "2026-10-20T09:00:00Z",
      accepted: 41,
    },
    {
      source: "tenant",
      key: "code-of-conduct",
      version: 2,
      title: "Code of conduct",
      body: "Be kind.",
      published_at: "2026-10-21T09:00:00Z",
      accepted: 7,
    },
  ],
};

describe("the console's documents page", () => {
  it("shows each current version with how many accepted it, and exports the acceptances as CSV [TF-028 AC2, ADR-0049]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["identity.documents.manage"])),
      "GET /api/v1/documents": () => json(documents),
      "GET /api/v1/documents/acceptances?page_number=1&page_size=50&sort=-accepted_at": () => json(emptyList),
    });
    renderWithProviders(<DocumentsPage />, { route: "/console/documents" });

    const table = await screen.findByRole("table", { name: /The current version of each document/ });
    const rows = within(table).getAllByRole("row").slice(1);
    expect(rows[0]).toHaveTextContent("Terms of service");
    expect(rows[0]).toHaveTextContent("The platform's");
    expect(rows[0]).toHaveTextContent("41");
    expect(rows[0]).toHaveTextContent("Published by the platform's operator");
    expect(rows[1]).toHaveTextContent("Your organisation's");
    expect(
      within(rows[1]!).getByRole("button", { name: "Publish the next version of Code of conduct" }),
    ).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Export as CSV" })).toHaveAttribute(
      "href",
      "/api/v1/documents/acceptances/export",
    );
    await waitFor(() =>
      expect(
        calls.some(
          (call) => call.key === "GET /api/v1/documents/acceptances?page_number=1&page_size=50&sort=-accepted_at",
        ),
      ).toBe(true),
    );
  });

  it("publishes the next version of a tenant document, which everyone accepts at their next request [TF-028 AC1, ADR-0049]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["identity.documents.manage"])),
      "GET /api/v1/documents": () => json(documents),
      "GET /api/v1/documents/acceptances?page_number=1&page_size=50&sort=-accepted_at": () => json(emptyList),
      "POST /api/v1/documents": () =>
        json(
          {
            source: "tenant",
            key: "code-of-conduct",
            version: 3,
            title: "Code of conduct",
            body: "Be kinder.",
            published_at: "2026-10-22T09:00:00Z",
          },
          201,
        ),
    });
    renderWithProviders(<DocumentsPage />, { route: "/console/documents" });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Publish the next version of Code of conduct" }));
    const dialog = await screen.findByRole("dialog", { name: "Publish the next version of Code of conduct" });
    expect(within(dialog).getByLabelText(/^Key/)).toHaveValue("code-of-conduct");
    await fill(user, within(dialog).getByLabelText(/^Text/), "Be kinder.");
    await user.click(within(dialog).getByRole("button", { name: "Publish" }));

    expect(
      await screen.findByText("Code of conduct version 3 is published. Everyone accepts it at their next request."),
    ).toBeInTheDocument();
    const sent = calls.find((call) => call.key === "POST /api/v1/documents");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({
      key: "code-of-conduct",
      title: "Code of conduct",
      body: "Be kinder.",
    });
  });
});
