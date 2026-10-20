import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { useSetting } from "./settings";

const entitlements = {
  revision: 1,
  plan: "enterprise",
  confirmed_at: "2026-10-20T09:00:00Z",
  stale: false,
  modules: [],
  features: [],
};

function Probe() {
  const idle = useSetting("identity.session.idle_minutes");
  const scheme = useSetting("shell.colour_scheme");
  return (
    <ul>
      <li data-testid="idle">{idle}</li>
      <li data-testid="scheme">{scheme}</li>
    </ul>
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("useSetting, the shell's one hook for settings (TF-059, ADR-0036)", () => {
  it("answers the effective value for the person from /me/context, typed by the declared keys [ADR-0036]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/me/context": () =>
        json({
          entitlements,
          settings: {
            values: [
              { key: "identity.session.idle_minutes", value: 30, level: "tenant" },
              { key: "shell.colour_scheme", value: "dark", level: "user" },
            ],
          },
        }),
    });
    renderWithProviders(<Probe />);

    await waitFor(() => expect(screen.getByTestId("idle")).toHaveTextContent("30"));
    expect(screen.getByTestId("scheme")).toHaveTextContent("dark");
  });

  // Untraced: the shell's fallback to the code default before /me/context answers and for a mistyped value is in the developer guide only, not in a criterion or ADR-0036
  it("answers the default in code until /me/context answers, when it fails, and for a value of another type [untraced]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/me/context": () =>
        json({
          entitlements,
          settings: { values: [{ key: "identity.session.idle_minutes", value: "thirty", level: "tenant" }] },
        }),
    });
    renderWithProviders(<Probe />);

    expect(screen.getByTestId("idle")).toHaveTextContent("60");
    await waitFor(() => expect(screen.getByTestId("scheme")).toHaveTextContent("auto"));
    expect(screen.getByTestId("idle")).toHaveTextContent("60");
  });

  // Untraced: the shell's fallback to the code defaults when /me/context fails is in the developer guide only; TF-059 AC4 and ADR-0036 speak of the unit's last values
  it("answers the defaults when the context call fails, and the shell still renders [untraced]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/me/context": () => problem(503, { title: "Unavailable", error_code: "test.down" }),
    });
    renderWithProviders(<Probe />);

    await waitFor(() => expect(screen.getByTestId("idle")).toHaveTextContent("60"));
    expect(screen.getByTestId("scheme")).toHaveTextContent("auto");
  });
});
