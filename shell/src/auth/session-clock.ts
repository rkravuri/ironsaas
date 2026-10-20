/**
 * The session's clock (TF-021, ADR-0033): when the session ends, when to warn, and what activity changes. Pure, so the
 * rules are tested on a fake clock; the warning component and the tabs' channel only feed it times and ask it questions.
 *
 * Every time here is on this browser's clock, in milliseconds. The API's times are on the server's clock; `timesFromMe`
 * moves them across with the offset the `Date` header gave, so a browser whose clock is wrong still warns on time.
 */

/** The warning comes this long before the end: long enough to read, decide and press one button (WCAG 2.2.1). */
export const warningLead = 2 * 60_000;

export interface SessionTimes {
  sessionId: string;
  /** When the session ends without further activity: the idle limit, never after the absolute one. */
  idleEndsAt: number;
  /** The absolute limit: nothing extends the session past it. */
  endsAt: number;
  /** How long one request keeps the session alive, as /me showed it; it moves the idle end after activity. */
  idleSpan: number;
}

/** The session as /me writes it. */
export interface MeSession {
  id: string;
  expires_at: string;
  idle_expires_at: string;
}

export type EndReason = "idle" | "absolute";

export type SessionPhase =
  | { kind: "active" }
  /** The idle end is near: one request extends it. */
  | { kind: "warning"; endsAt: number }
  /** The absolute end is near: nothing extends it. */
  | { kind: "final"; endsAt: number }
  | { kind: "ended"; reason: EndReason };

/**
 * The times of a /me answer. `serverOffset` is the server's clock minus this browser's (zero when unknown). The idle span
 * is what the answer shows between now and the idle end. The server writes its activity time at most once a minute, so
 * one answer can show less than the whole span and never more: the largest seen for the session is kept. When the idle
 * end is the absolute limit, the answer says nothing about the span.
 */
export function timesFromMe(
  session: MeSession,
  receivedAt: number,
  serverOffset: number,
  previous?: SessionTimes,
): SessionTimes {
  const endsAt = Date.parse(session.expires_at) - serverOffset;
  const idleEndsAt = Math.min(Date.parse(session.idle_expires_at) - serverOffset, endsAt);
  const shown = idleEndsAt - receivedAt;
  const known = previous !== undefined && previous.sessionId === session.id ? previous.idleSpan : undefined;
  const capped = idleEndsAt >= endsAt;
  const idleSpan = known === undefined ? shown : capped ? known : Math.max(known, shown);
  return { sessionId: session.id, idleEndsAt, endsAt, idleSpan: Math.max(0, idleSpan) };
}

/**
 * A request answered at `at` kept the session alive: the idle end moves to `at` plus the span, never past the absolute
 * limit and never back. The server writes its activity time at most once a minute, so this estimate can be up to that
 * minute late; the warning's two minutes leave the person more than one to answer.
 */
export function afterActivity(times: SessionTimes, at: number): SessionTimes {
  const idleEndsAt = Math.min(Math.max(times.idleEndsAt, at + times.idleSpan), times.endsAt);
  return idleEndsAt === times.idleEndsAt ? times : { ...times, idleEndsAt };
}

/**
 * What another tab knows, joined with what this tab knows: for the same session the later idle end wins, because
 * activity in any tab extends every tab. Another session's times replace nothing; that tab signed in afresh.
 */
export function merge(mine: SessionTimes | undefined, theirs: SessionTimes): SessionTimes | undefined {
  if (mine === undefined || mine.sessionId !== theirs.sessionId) {
    return mine;
  }
  if (theirs.idleEndsAt <= mine.idleEndsAt) {
    return mine;
  }
  return {
    ...mine,
    idleEndsAt: Math.min(theirs.idleEndsAt, mine.endsAt),
    idleSpan: Math.max(mine.idleSpan, theirs.idleSpan),
  };
}

/** Where the session stands at `now`. Near the absolute limit the warning is the final one, whatever the idle end says. */
export function phaseAt(times: SessionTimes, now: number): SessionPhase {
  const end = Math.min(times.idleEndsAt, times.endsAt);
  if (now >= end) {
    return { kind: "ended", reason: times.endsAt <= times.idleEndsAt ? "absolute" : "idle" };
  }
  if (times.endsAt - now <= warningLead) {
    return { kind: "final", endsAt: end };
  }
  if (times.idleEndsAt - now <= warningLead) {
    return { kind: "warning", endsAt: times.idleEndsAt };
  }
  return { kind: "active" };
}

/** How long until the phase can change, for one timer; undefined once the session has ended. */
export function nextChangeIn(times: SessionTimes, now: number): number | undefined {
  const phase = phaseAt(times, now);
  const end = Math.min(times.idleEndsAt, times.endsAt);
  let next: number;
  switch (phase.kind) {
    case "ended":
      return undefined;
    case "active":
      next = Math.min(times.idleEndsAt, times.endsAt) - warningLead;
      break;
    case "warning":
      next = Math.min(times.endsAt - warningLead, end);
      break;
    case "final":
      next = end;
      break;
  }
  return Math.max(0, next - now);
}
