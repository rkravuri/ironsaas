import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import { json, problem, renderWithProviders, stubFetch } from "@/test/render";
import { leaveFor } from "./leave";
import { SignInLinkPage } from "./SignInLinkPage";

vi.mock("./leave", () => ({ leaveFor: vi.fn() }));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(leaveFor).mockReset();
});

describe("the one-time sign-in link of the operator's path", () => {
  it("sends the code from the fragment once, in the body, and starts the shell again signed in [TF-026 AC1, ADR-0049]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => problem(401, { error_code: "auth.not_authenticated" }),
      "POST /auth/sign-in-link": () => json({ principal: {}, session: {} }),
    });
    renderWithProviders(<SignInLinkPage />, { route: "/sign-in-link#a-one-time-code" });

    await waitFor(() => expect(leaveFor).toHaveBeenCalledWith("/"));
    const sent = calls.filter((call) => call.key === "POST /auth/sign-in-link");
    expect(sent).toHaveLength(1);
    expect(JSON.parse(sent[0]?.init?.body as string)).toEqual({ code: "a-one-time-code" });
  });

  it("says a used or expired link does not work, and where to go [ADR-0049]", async () => {
    stubFetch({
      "GET /api/v1/me": () => problem(401, { error_code: "auth.not_authenticated" }),
      "POST /auth/sign-in-link": () => problem(404, { error_code: "identity.sign_in_link.invalid" }, "corr-link"),
    });
    renderWithProviders(<SignInLinkPage />, { route: "/sign-in-link#used" });

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(
      "This sign-in link does not work: it was used, it expired, or it was never issued.",
    );
    expect(alert).toHaveTextContent("corr-link");
    expect(screen.getByRole("link", { name: "Sign in" })).toHaveAttribute("href", "/login");
    expect(leaveFor).not.toHaveBeenCalled();
  });
});
