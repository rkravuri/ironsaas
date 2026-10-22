import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { request } from "@/api/client";
import { sessionEndOf } from "@/auth/session-channel";
import { EmergencyBanner } from "./EmergencyBanner";
import { ImpersonationBand } from "./Impersonation";

afterEach(() => {
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

const impersonated = (operator = false) => ({
  ...me([]),
  display_name: "Pat Person",
  impersonation: {
    impersonator_name: "Ada Admin",
    until: "2026-10-22T12:00:00Z",
    reason: "Their invoices do not show",
    operator,
  },
});

describe("the impersonation band", () => {
  it("says on every page whose account this is, until when and why, and ends it at once [TF-025 AC2, TF-025 AC3, ADR-0049]", async () => {
    let ended = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(ended ? { ...me([]), display_name: "Ada Admin" } : impersonated()),
      "POST /api/v1/me/impersonation/end": () => {
        ended = true;
        return new Response(null, { status: 204 });
      },
    });
    renderWithProviders(<ImpersonationBand />);
    const user = userEvent.setup();

    const band = await screen.findByRole("status");
    expect(band).toHaveTextContent("You are signed in as Pat Person");
    expect(band).toHaveTextContent("You, Ada Admin, are signed in as this person until");
    expect(band).toHaveTextContent("Reason: Their invoices do not show");
    expect(band).toHaveTextContent("You cannot confirm who you are, sign, approve");
    await user.click(within(band).getByRole("button", { name: "End" }));

    await waitFor(() => expect(calls.some((call) => call.key === "POST /api/v1/me/impersonation/end")).toBe(true));
    expect(
      await screen.findByText("You are yourself again: the time to sign in as that person has ended."),
    ).toBeInTheDocument();
  });

  it("says when the API ended the impersonation at its time, and runs as the impersonator again [TF-025 AC3, ADR-0049]", async () => {
    let afterTime = false;
    stubFetch({
      "GET /api/v1/me": () => json(afterTime ? { ...me([]), display_name: "Ada Admin" } : impersonated()),
      "GET /api/v1/roles": () => json({}, 200, { "Iron-Impersonation-Ended": "true" }),
    });
    renderWithProviders(<ImpersonationBand />);

    expect(await screen.findByText("You are signed in as Pat Person")).toBeInTheDocument();
    afterTime = true;
    await request("/api/v1/roles");

    expect(
      await screen.findByText("You are yourself again: the time to sign in as that person has ended."),
    ).toBeInTheDocument();
    expect(screen.queryByText("You are signed in as Pat Person")).toBeNull();
  });

  it("names the operator as the platform's when they signed in through the control plane [TF-025 AC2, ADR-0049]", async () => {
    stubFetch({ "GET /api/v1/me": () => json(impersonated(true)) });
    renderWithProviders(<ImpersonationBand />);

    expect(await screen.findByRole("status")).toHaveTextContent(
      "You, Ada Admin, the platform's operator, are signed in as this person",
    );
  });

  it("turns each 401 into the reason the sign-in page gives [TF-021 AC2, TF-022 AC1, ADR-0049]", () => {
    expect(sessionEndOf("auth.session_limit")).toBe("limit");
    expect(sessionEndOf("auth.session_ended")).toBe("ended");
    expect(sessionEndOf("auth.session_revoked")).toBe("revoked");
    expect(sessionEndOf("auth.impersonation_ended")).toBe("impersonation-ended");
    expect(sessionEndOf("auth.session_expired")).toBe("expired");
    expect(sessionEndOf(undefined)).toBe("expired");
  });
});

describe("the emergency account banner", () => {
  const alerts = {
    emergency_sign_ins: [
      {
        id: "e1",
        principal_id: "p1",
        display_name: "Erin Emergency",
        login_id: "erin@acme.localhost",
        at: "2026-10-22T09:00:00Z",
      },
    ],
  };

  it("shows the administrators every emergency sign-in of the last day until they dismiss it here [ADR-0049]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me(["identity.users.manage"])),
      "GET /api/v1/security-alerts": () => json(alerts),
    });
    renderWithProviders(<EmergencyBanner />);
    const user = userEvent.setup();

    const banner = await screen.findByRole("alert");
    expect(banner).toHaveTextContent("An emergency account signed in");
    expect(banner).toHaveTextContent("Erin Emergency (erin@acme.localhost) signed in at");
    expect(within(banner).getByRole("link", { name: "Open the account" })).toHaveAttribute("href", "/users/p1");
    await user.click(within(banner).getByRole("button", { name: "Dismiss in this browser" }));

    await waitFor(() => expect(screen.queryByRole("alert")).toBeNull());
    expect(JSON.parse(window.localStorage.getItem("iron.emergency-alerts.dismissed") ?? "[]")).toEqual(["e1"]);
  });

  it("asks nothing for someone who does not manage people [ADR-0049]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me(["identity.users.read"])),
      "GET /api/v1/security-alerts": () => json(alerts),
    });
    renderWithProviders(<EmergencyBanner />);

    await waitFor(() => expect(calls.some((call) => call.key === "GET /api/v1/me")).toBe(true));
    await new Promise((settled) => setTimeout(settled, 50));
    expect(calls.some((call) => call.key === "GET /api/v1/security-alerts")).toBe(false);
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
