import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import { useFeature } from "@/auth/entitlements";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { useFlag } from "./flags";

const entitlements = {
  revision: 1,
  plan: "enterprise",
  confirmed_at: "2026-10-20T09:00:00Z",
  stale: false,
  modules: [],
  features: [{ code: "authorization.role_transfer", status: "enabled", reason: "licensed" }],
};

const flags = (release: boolean, tone: string) => ({
  known: true,
  confirmed_at: "2026-10-20T09:00:00Z",
  values: [
    { key: "flags.example_release", type: "boolean", value: release, reason: release ? "split" : "default" },
    { key: "flags.example_tone", type: "string", value: tone, reason: "targeting_match" },
    { key: "flags.example_limits", type: "json", value: { rows: 50 }, reason: "targeting_match" },
  ],
  kill_switches: [],
});

const objects: unknown[] = [];

function Probe() {
  const release = useFlag("flags.example_release", false);
  const tone = useFlag("flags.example_tone", "plain");
  // A number default for a flag that is a string: the type does not fit, so the default stands.
  const mistyped = useFlag("flags.example_tone", 7);
  const missing = useFlag("flags.not_in_this_build", "fallback");
  const limits = useFlag("flags.example_limits", { rows: 10 });
  objects.push(limits);
  const transfer = useFeature("authorization.role_transfer");
  return (
    <ul>
      <li data-testid="release">{String(release)}</li>
      <li data-testid="tone">{tone}</li>
      <li data-testid="mistyped">{mistyped}</li>
      <li data-testid="missing">{missing}</li>
      <li data-testid="limits">{limits.rows}</li>
      <li data-testid="transfer">{transfer.available ? "available" : "unavailable"}</li>
    </ul>
  );
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  objects.length = 0;
});

describe("useFlag, the shell's one hook for flags (TF-054, ADR-0035)", () => {
  it("answers each flag from /me/context through OpenFeature, and follows a change within 30 seconds", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let answer = { entitlements, flags: flags(false, "warm") };
    stubFetch({ "GET /api/v1/me": () => json(me()), "GET /api/v1/me/context": () => json(answer) });
    renderWithProviders(<Probe />);

    await waitFor(() => expect(screen.getByTestId("tone")).toHaveTextContent("warm"));
    expect(screen.getByTestId("release")).toHaveTextContent("false");
    expect(screen.getByTestId("mistyped")).toHaveTextContent("7");
    expect(screen.getByTestId("missing")).toHaveTextContent("fallback");
    expect(screen.getByTestId("limits")).toHaveTextContent("50");

    // The operator turns the release on for this tenant; the next read of the context brings it.
    answer = { entitlements, flags: flags(true, "warm") };
    await vi.advanceTimersByTimeAsync(30_000);

    await waitFor(() => expect(screen.getByTestId("release")).toHaveTextContent("true"));
    // The same JSON keeps the same object across renders, so a component that depends on it does not loop.
    const fifty = objects.filter((value) => (value as { rows: number }).rows === 50);
    expect(new Set(fifty).size).toBe(1);
  });

  it("gives every flag its default when /me/context fails, and the shell still renders", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/me/context": () => problem(503, { title: "Unavailable", error_code: "test.down" }),
    });
    renderWithProviders(<Probe />);

    await waitFor(() => expect(screen.getByTestId("tone")).toHaveTextContent("plain"));
    expect(screen.getByTestId("release")).toHaveTextContent("false");
    expect(screen.getByTestId("limits")).toHaveTextContent("10");
  });

  it("gives the defaults when the flags part is missing or does not parse, and keeps the entitlements", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      "GET /api/v1/me/context": () => json({ entitlements, flags: { known: "yes" } }),
    });
    renderWithProviders(<Probe />);

    await waitFor(() => expect(screen.getByTestId("transfer")).toHaveTextContent("available"));
    expect(screen.getByTestId("tone")).toHaveTextContent("plain");
    expect(screen.getByTestId("release")).toHaveTextContent("false");
  });

  it("forgets the values when the session ends: signed out, every flag has its default", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let signedIn = true;
    const unauthorized = () => problem(401, { title: "Unauthorized", error_code: "identity.session.required" });
    stubFetch({
      "GET /api/v1/me": () => (signedIn ? json(me()) : unauthorized()),
      "GET /api/v1/me/context": () => (signedIn ? json({ entitlements, flags: flags(true, "warm") }) : unauthorized()),
    });
    renderWithProviders(<Probe />);
    await waitFor(() => expect(screen.getByTestId("tone")).toHaveTextContent("warm"));

    // The session ends on the server; the next read of the context answers 401 and the shell forgets it.
    signedIn = false;
    await vi.advanceTimersByTimeAsync(30_000);

    await waitFor(() => expect(screen.getByTestId("tone")).toHaveTextContent("plain"));
    expect(screen.getByTestId("release")).toHaveTextContent("false");
  });
});
