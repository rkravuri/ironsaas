import { createContext, useContext } from "react";
import type { ResponseInfo } from "@/api/client";
import type { SessionChannel } from "./session-channel";
import { afterActivity, merge, timesFromMe, type MeSession, type SessionTimes } from "./session-clock";

/** Another tab hears of a moved idle end only when it moved this much: enough for a two-minute warning, few messages. */
const publishStep = 15_000;

/**
 * One tab's knowledge of the session's times (ADR-0033): /me sets them, every answer of the API moves the idle end, and
 * the other tabs hear of it. A store outside React, so that an answer re-renders the warning and nothing else.
 */
export class SessionWatch {
  private times: SessionTimes | undefined;
  private published: SessionTimes | undefined;
  private offset = 0;
  private channel: SessionChannel | undefined;
  private readonly listeners = new Set<() => void>();

  /** The tabs' channel, opened and closed by the provider's effect; without one, this tab keeps its news to itself. */
  attach(channel: SessionChannel): () => void {
    this.channel = channel;
    return () => {
      if (this.channel === channel) {
        this.channel = undefined;
      }
    };
  }

  readonly getTimes = (): SessionTimes | undefined => this.times;

  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  /** A /me answer: the truth for this tab, on this browser's clock, and news for the others. */
  fromMe(session: MeSession, receivedAt: number): void {
    this.set(timesFromMe(session, receivedAt, this.offset, this.times), true);
  }

  /** Any answer: the server's clock from its `Date`, and, unless it says the session is gone, activity. */
  response(info: ResponseInfo): void {
    if (info.serverDate !== undefined) {
      this.offset = info.serverDate - info.receivedAt;
    }
    // A 401 means no session; a 5xx may not have reached it. Before the first /me there is nothing to move.
    if (this.times === undefined || info.status === 401 || info.status >= 500) {
      return;
    }
    this.set(afterActivity(this.times, info.receivedAt), false);
  }

  /** Another tab's times: the later idle end of the same session wins. */
  heard(times: SessionTimes): void {
    const next = merge(this.times, times);
    if (next !== this.times) {
      this.times = next;
      this.published = next;
      this.notify();
    }
  }

  /** The session ended or another began: forget the times until the next /me. */
  clear(): void {
    this.times = undefined;
    this.published = undefined;
    this.notify();
  }

  private set(next: SessionTimes, always: boolean): void {
    if (next === this.times) {
      return;
    }
    this.times = next;
    const last = this.published;
    if (always || last === undefined || last.sessionId !== next.sessionId || Math.abs(next.idleEndsAt - last.idleEndsAt) >= publishStep) {
      this.published = next;
      this.channel?.post({ kind: "times", times: next });
    }
    this.notify();
  }

  private notify(): void {
    for (const listener of this.listeners) {
      listener();
    }
  }
}

export const SessionWatchContext = createContext<SessionWatch | undefined>(undefined);

export function useSessionWatch(): SessionWatch {
  const watch = useContext(SessionWatchContext);
  if (!watch) {
    throw new Error("useSessionWatch needs a SessionProvider above it.");
  }
  return watch;
}
