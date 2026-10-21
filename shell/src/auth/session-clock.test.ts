import { describe, expect, it } from "vitest";
import {
  afterActivity,
  merge,
  nextChangeIn,
  phaseAt,
  timesFromMe,
  warningLead,
  type SessionTimes,
} from "./session-clock";

const minute = 60_000;
const hour = 60 * minute;
/** The fake clock's start: a login at 08:00 on the server's clock and on this browser's, unless a test says otherwise. */
const start = Date.parse("2026-10-19T08:00:00Z");
const iso = (at: number) => new Date(at).toISOString();

/** A session as /me shows it right after a login at `start`: idle in 60 minutes, absolute in 12 hours. */
const fresh = (): SessionTimes =>
  timesFromMe({ id: "s1", idle_expires_at: iso(start + hour), expires_at: iso(start + 12 * hour) }, start, 0);

describe("the session clock", () => {
  it("reads /me's times onto this browser's clock, with the idle span it shows [ADR-0033]", () => {
    expect(fresh()).toEqual({ sessionId: "s1", idleEndsAt: start + hour, endsAt: start + 12 * hour, idleSpan: hour });

    // The server's clock runs five minutes ahead of this browser's: the ends move back by five minutes, the span stays.
    const skewed = timesFromMe(
      { id: "s1", idle_expires_at: iso(start + 5 * minute + hour), expires_at: iso(start + 5 * minute + 12 * hour) },
      start,
      5 * minute,
    );
    expect(skewed.idleEndsAt).toBe(start + hour);
    expect(skewed.endsAt).toBe(start + 12 * hour);
    expect(skewed.idleSpan).toBe(hour);
  });

  it("keeps the largest idle span it has seen: an answer between two touches shows less [ADR-0033]", () => {
    const touched = fresh();
    // Thirty seconds later the server has not written its activity time again: the answer shows a span 30 seconds short.
    const between = timesFromMe(
      { id: "s1", idle_expires_at: iso(start + hour), expires_at: iso(start + 12 * hour) },
      start + 30_000,
      0,
      touched,
    );
    expect(between.idleSpan).toBe(hour);
    expect(between.idleEndsAt).toBe(start + hour);
  });

  it("keeps the idle span it knew when the idle end is the absolute limit [ADR-0033]", () => {
    const near = start + 11.5 * hour;
    const capped = timesFromMe(
      { id: "s1", idle_expires_at: iso(start + 12 * hour), expires_at: iso(start + 12 * hour) },
      near,
      0,
      fresh(),
    );
    expect(capped.idleEndsAt).toBe(start + 12 * hour);
    expect(capped.idleSpan).toBe(hour);
  });

  it("is quiet until two minutes before the idle end, warns, then ends for idleness [ADR-0033]", () => {
    const times = fresh();
    expect(phaseAt(times, start)).toEqual({ kind: "active" });
    expect(nextChangeIn(times, start)).toBe(hour - warningLead);

    const warnedAt = start + hour - warningLead;
    expect(phaseAt(times, warnedAt - 1)).toEqual({ kind: "active" });
    expect(phaseAt(times, warnedAt)).toEqual({ kind: "warning", endsAt: start + hour });
    expect(nextChangeIn(times, warnedAt)).toBe(warningLead);

    expect(phaseAt(times, start + hour)).toEqual({ kind: "ended", reason: "idle" });
    expect(nextChangeIn(times, start + hour)).toBeUndefined();
  });

  it("moves the idle end with activity, never back and never past the absolute limit [ADR-0033]", () => {
    const times = fresh();
    const later = afterActivity(times, start + 30 * minute);
    expect(later.idleEndsAt).toBe(start + 90 * minute);
    // An answer that arrives late, from before the last one, changes nothing.
    expect(afterActivity(later, start + 10 * minute)).toBe(later);
    // Close to the absolute limit the idle end stops there.
    expect(afterActivity(times, start + 11.5 * hour).idleEndsAt).toBe(start + 12 * hour);
  });

  it("gives the final warning near the absolute limit, which activity cannot move [ADR-0033]", () => {
    const busy = afterActivity(fresh(), start + 12 * hour - minute);
    const now = start + 12 * hour - warningLead;
    expect(phaseAt(busy, now)).toEqual({ kind: "final", endsAt: start + 12 * hour });
    expect(phaseAt(afterActivity(busy, now + 30_000), now + 30_000)).toEqual({
      kind: "final",
      endsAt: start + 12 * hour,
    });
    expect(nextChangeIn(busy, now)).toBe(warningLead);
    expect(phaseAt(busy, start + 12 * hour)).toEqual({ kind: "ended", reason: "absolute" });
  });

  it("gives only the final warning when the idle end is the absolute limit [ADR-0033]", () => {
    // Active until three minutes before the absolute limit: the idle end is that limit, so the one warning is the final one.
    const times = afterActivity(fresh(), start + 12 * hour - 3 * minute);
    expect(phaseAt(times, start + 12 * hour - 2.5 * minute)).toEqual({ kind: "active" });
    expect(nextChangeIn(times, start + 12 * hour - 2.5 * minute)).toBe(30_000);
    expect(phaseAt(times, start + 12 * hour - 2 * minute).kind).toBe("final");
  });

  it("joins what another tab knows: the later idle end of the same session wins [ADR-0033]", () => {
    const mine = fresh();
    const theirs = afterActivity(mine, start + 40 * minute);
    expect(merge(mine, theirs)?.idleEndsAt).toBe(start + 100 * minute);
    expect(merge(theirs, mine)).toBe(theirs);
    expect(merge(mine, { ...theirs, sessionId: "s2" })).toBe(mine);
    expect(merge(undefined, theirs)).toBeUndefined();
  });

  it("closes a warning in this tab when another tab's activity arrives [ADR-0033]", () => {
    const mine = fresh();
    const now = start + 59 * minute;
    expect(phaseAt(mine, now).kind).toBe("warning");
    const joined = merge(mine, afterActivity(mine, now));
    expect(joined && phaseAt(joined, now)).toEqual({ kind: "active" });
  });
});
