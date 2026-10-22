import { useEffect, useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { Text } from "@mantine/core";
import { request } from "@/api/client";
import { RequireEnrolled } from "@/shell/Guards";
import { EnrolmentPage } from "./EnrolmentPage";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";

afterEach(() => {
  vi.unstubAllGlobals();
});

/** A page of the frame that reads users on arrival, as the Users page does. */
function UsersProbe() {
  const [loaded, setLoaded] = useState("loading");
  useEffect(() => {
    request("/api/v1/users").then(
      () => setLoaded("users loaded"),
      () => setLoaded("users refused"),
    );
  }, []);
  return <Text>{loaded}</Text>;
}

const codes = Array.from({ length: 10 }, (_, i) => `AAAA-BBBB-CCCC-${String(i).padStart(4, "2")}`);

describe("enrolment required in the session", () => {
  it("takes the person from a refused page to enrolment and back to it once they enrolled, signed in all along [TF-013 AC1, ADR-0047]", async () => {
    let enrolled = false;
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json({ ...me(["identity.users.read"]), mfa_enrolment_required: !enrolled }),
      "GET /api/v1/users": () =>
        enrolled
          ? json({ data: [], meta: {}, links: {} })
          : problem(403, { error_code: "identity.mfa.enrolment_required" }, "corr-enrol"),
      "GET /api/v1/me/factors": () =>
        json({ factors: [], recovery_codes_left: 0, required: true, allowed: ["authenticator_app"], enrol_by: null }),
      "POST /api/v1/me/factors/authenticator-app": () =>
        json({
          secret: "JBSWY3DPEHPK3PXP",
          uri: "otpauth://totp/IronSaaS:ada?secret=JBSWY3DPEHPK3PXP",
          expires_at: "2026-10-22T10:15:00Z",
        }),
      "POST /api/v1/me/factors/authenticator-app/confirm": () => {
        enrolled = true;
        return json({
          factor: {
            id: "f1",
            kind: "authenticator_app",
            name: "Phone",
            created_at: "2026-10-22T10:00:00Z",
            last_used_at: null,
          },
          recovery_codes: codes,
        });
      },
    });
    renderWithProviders(
      <Routes>
        <Route
          path="/users"
          element={
            <RequireEnrolled>
              <UsersProbe />
            </RequireEnrolled>
          }
        />
        <Route path="/enrol" element={<EnrolmentPage />} />
        <Route path="/login" element={<p>login page</p>} />
      </Routes>,
      { route: "/users" },
    );
    const user = userEvent.setup();

    await screen.findByRole("heading", { name: "Set up a second factor" });
    expect(await screen.findByText(/now requires a second factor of you/)).toBeInTheDocument();
    await fill(user, await screen.findByLabelText(/code the app shows now/i), "123456");
    await user.click(screen.getByRole("button", { name: "Save the authenticator app" }));
    const list = await screen.findByRole("list", { name: "Your recovery codes" });
    expect(within(list).getAllByRole("listitem")).toHaveLength(10);
    await user.click(screen.getByLabelText("I have saved my recovery codes"));
    await user.click(screen.getByRole("button", { name: "Continue" }));

    // After Continue: /me read again, the way back, and the page's call; under the whole suite's load more than a second.
    expect(await screen.findByText("users loaded", {}, { timeout: 5_000 })).toBeInTheDocument();
    expect(screen.queryByText("login page")).toBeNull();
    expect(calls.filter((call) => call.key === "GET /api/v1/users").length).toBeGreaterThanOrEqual(1);
  });

  it("goes back at once when nothing is required any more [ADR-0047]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json({ ...me(), mfa_enrolment_required: false }),
      "GET /api/v1/me/factors": () =>
        json({ factors: [], recovery_codes_left: 0, required: false, allowed: ["authenticator_app"], enrol_by: null }),
    });
    renderWithProviders(
      <Routes>
        <Route path="/" element={<p>home</p>} />
        <Route path="/enrol" element={<EnrolmentPage />} />
      </Routes>,
      { route: "/enrol" },
    );

    expect(await screen.findByText("home")).toBeInTheDocument();
  });
});
