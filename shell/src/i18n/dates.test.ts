import { afterEach, describe, expect, it } from "vitest";
import { currentTimeZone, formatDateTime, formatDay, formatTime, setTimeZone, startOfDay } from "./dates";

afterEach(() => {
  setTimeZone(undefined);
});

describe("the dates the shell shows", () => {
  it("shows a stored time in the person's zone and names the zone [TF-065 AC2, ADR-0050]", () => {
    setTimeZone("Asia/Kolkata");
    expect(formatDateTime("2026-10-22T09:00:00Z")).toBe("Oct 22, 2026, 2:30 PM GMT+5:30");
    expect(formatTime("2026-10-22T09:00:00Z")).toBe("2:30 PM GMT+5:30");

    setTimeZone("America/New_York");
    expect(formatDateTime("2026-10-22T09:00:00Z")).toBe("Oct 22, 2026, 5:00 AM EDT");
  });

  it("follows the browser's zone when the person has chosen none, or chose one the browser does not know [TF-065 AC2, ADR-0050]", () => {
    const browser = Intl.DateTimeFormat().resolvedOptions().timeZone;
    setTimeZone("");
    expect(currentTimeZone()).toBe(browser);
    setTimeZone("Mars/Olympus_Mons");
    expect(currentTimeZone()).toBe(browser);
  });

  it("shows a calendar day as that day in every zone [ADR-0050]", () => {
    setTimeZone("Pacific/Honolulu");
    expect(formatDay("2026-10-22")).toBe("Oct 22, 2026");
    setTimeZone("Pacific/Kiritimati");
    expect(formatDay("2026-10-22")).toBe("Oct 22, 2026");
  });

  it("starts a searched day at midnight in the person's zone, also on the day the clocks go back [TF-065 AC2, ADR-0050]", () => {
    setTimeZone("Asia/Kolkata");
    expect(startOfDay("2026-10-22").toISOString()).toBe("2026-10-21T18:30:00.000Z");
    setTimeZone("Europe/London");
    expect(startOfDay("2026-10-25").toISOString()).toBe("2026-10-24T23:00:00.000Z");
    expect(startOfDay("2026-10-26").toISOString()).toBe("2026-10-26T00:00:00.000Z");
  });
});
