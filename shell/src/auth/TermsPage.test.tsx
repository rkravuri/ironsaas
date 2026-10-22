import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { RequireTerms } from "@/shell/Guards";
import { LoginPage } from "./LoginPage";
import { TermsPage } from "./TermsPage";

afterEach(() => {
  vi.unstubAllGlobals();
});

const terms = {
  source: "platform",
  key: "terms_of_service",
  version: 3,
  title: "Terms of service",
  body: "The platform's terms, version three.",
  published_at: "2026-10-20T09:00:00Z",
};
const conduct = {
  source: "tenant",
  key: "code-of-conduct",
  version: 1,
  title: "Code of conduct",
  body: "Be kind.",
  published_at: "2026-10-21T09:00:00Z",
};

const app = (route: string) =>
  renderWithProviders(
    <Routes>
      <Route path="/terms" element={<TermsPage />} />
      <Route path="/login" element={<LoginPage />} />
      <Route
        path="/orders"
        element={
          <RequireTerms>
            <p>The orders page</p>
          </RequireTerms>
        }
      />
    </Routes>,
    { route },
  );

describe("terms acceptance", () => {
  it("holds every page back until the new versions are accepted, then returns to the page [TF-028 AC1, ADR-0049]", async () => {
    let accepted = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json({ ...me([]), terms_acceptance_required: !accepted }),
      "GET /api/v1/me/terms": () => json({ pending: accepted ? [] : [terms, conduct] }),
      "POST /api/v1/me/terms/accept": () => {
        accepted = true;
        return new Response(null, { status: 204 });
      },
    });
    app("/orders");
    const user = userEvent.setup();

    expect(await screen.findByRole("heading", { level: 2, name: "Read and accept" })).toBeInTheDocument();
    expect(screen.queryByText("The orders page")).toBeNull();
    const article = await screen.findByRole("article", { name: "Terms of service" });
    expect(article).toHaveTextContent("The platform's");
    expect(article).toHaveTextContent("Version 3");
    expect(article).toHaveTextContent("The platform's terms, version three.");
    expect(screen.getByRole("article", { name: "Code of conduct" })).toHaveTextContent("Your organisation's");

    await user.click(screen.getByRole("button", { name: "Accept all 2" }));

    expect(await screen.findByText("The orders page")).toBeInTheDocument();
    const sent = calls.find((call) => call.key === "POST /api/v1/me/terms/accept");
    expect(JSON.parse(sent?.init?.body as string)).toEqual({
      documents: [
        { source: "platform", key: "terms_of_service", version: 3 },
        { source: "tenant", key: "code-of-conduct", version: 1 },
      ],
    });
  });

  it("shows a newer version that came while the person read, rather than accepting an old one [TF-028 AC1]", async () => {
    let refused = false;
    stubFetch({
      "GET /api/v1/me": () => json({ ...me([]), terms_acceptance_required: true }),
      "GET /api/v1/me/terms": () => json({ pending: [refused ? { ...terms, version: 4 } : terms] }),
      "POST /api/v1/me/terms/accept": () => {
        refused = true;
        return problem(
          409,
          {
            error_code: "identity.terms.version_changed",
            detail: "A newer version may have been published while you read; read it first.",
          },
          "corr-new",
        );
      },
    });
    app("/terms");
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Accept" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("A newer version may have been published");
    await waitFor(() =>
      expect(screen.getByRole("article", { name: "Terms of service" })).toHaveTextContent("Version 4"),
    );
  });

  it("signs the person out when they decline, and the sign-in page says why [TF-028 AC4, ADR-0049]", async () => {
    let declined = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () =>
        declined
          ? problem(401, { error_code: "auth.not_authenticated" })
          : json({ ...me([]), terms_acceptance_required: true }),
      "GET /api/v1/me/terms": () => json({ pending: [terms] }),
      "POST /api/v1/me/terms/decline": () => {
        declined = true;
        return new Response(null, { status: 204 });
      },
      "GET /auth/sign-in-options": () => json({ passkey_sign_in: false, providers: [] }),
    });
    app("/terms");
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Decline and sign out" }));
    const dialog = await screen.findByRole("dialog", { name: "Decline and sign out?" });
    expect(dialog).toHaveTextContent("You cannot use the service without accepting.");
    await user.click(within(dialog).getByRole("button", { name: "Decline and sign out" }));

    await waitFor(() => expect(calls.some((call) => call.key === "POST /api/v1/me/terms/decline")).toBe(true));
  });
});
