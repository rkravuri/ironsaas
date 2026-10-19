import type { SessionTimes } from "./session-clock";

/** Why a session ended, as the login page explains it. */
export type SessionEnd = "idle" | "absolute" | "signed-out" | "signed-out-elsewhere" | "expired";

/** What the tabs of one browser tell each other about the session they share (ADR-0033). */
export type SessionMessage =
  /** This tab knows a later idle end: activity here extends every tab. */
  | { kind: "times"; times: SessionTimes }
  /** The session ended in this tab: the others follow it to the login page. */
  | { kind: "ended"; reason: SessionEnd }
  /** Somebody signed in from this tab: a tab still on the login page reads /me again. */
  | { kind: "signed-in" };

export interface SessionChannel {
  post: (message: SessionMessage) => void;
  listen: (listener: (message: SessionMessage) => void) => () => void;
  close: () => void;
}

export const sessionChannelName = "iron.session";

/** A tab without BroadcastChannel keeps to itself: nothing is sent and nothing arrives. */
const alone: SessionChannel = { post: () => undefined, listen: () => () => undefined, close: () => undefined };

/** The tabs' channel: messages reach every other tab of this origin, never the tab that sent them. */
export function openSessionChannel(): SessionChannel {
  if (typeof BroadcastChannel === "undefined") {
    return alone;
  }
  const channel = new BroadcastChannel(sessionChannelName);
  return {
    post: (message) => channel.postMessage(message),
    listen: (listener) => {
      const receive = (event: MessageEvent<SessionMessage>) => listener(event.data);
      channel.addEventListener("message", receive);
      return () => channel.removeEventListener("message", receive);
    },
    close: () => channel.close(),
  };
}
