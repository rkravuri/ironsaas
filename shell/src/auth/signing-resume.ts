import { useEffect, useState } from "react";

/**
 * A signature waiting for a company login's fresh sign-in (ADR-0051, the T7 review). Before the page leaves for the
 * provider, the signing dialog keeps what it was signing: the page, the document type, the meaning, the comment and the
 * page's own description of the record. When the provider sends the person back to that page, the page reopens the
 * dialog with all of it, ready to confirm. Kept in this tab only, for ten minutes; nothing is kept when the browser
 * keeps nothing, and the person then starts the signature again.
 */
export interface PendingSigning {
  path: string;
  recordType: string;
  meaning: string;
  comment: string;
  /** What the page needs to show the record again; the page's own, as plain data. */
  context: unknown;
  savedAt: number;
}

const storageKey = "iron.signing.pending";
const keptFor = 10 * 60_000;

const here = () => window.location.pathname + window.location.search;

export function rememberSigning(pending: Omit<PendingSigning, "path" | "savedAt">): void {
  try {
    window.sessionStorage.setItem(storageKey, JSON.stringify({ ...pending, path: here(), savedAt: Date.now() }));
  } catch {
    // Storage refused (a private window, a full store): the person signs again after their sign-in.
  }
}

/** The signature this page left for the provider to confirm, when there is one for this document type. */
export function pendingSigning(recordType: string): PendingSigning | undefined {
  try {
    const text = window.sessionStorage.getItem(storageKey);
    const pending = text ? (JSON.parse(text) as PendingSigning) : undefined;
    return pending &&
      pending.recordType === recordType &&
      pending.path === here() &&
      Date.now() - pending.savedAt < keptFor
      ? pending
      : undefined;
  } catch {
    return undefined;
  }
}

export function forgetSigning(): void {
  try {
    window.sessionStorage.removeItem(storageKey);
  } catch {
    // Nothing to forget.
  }
}

/**
 * The signature to reopen on this page, read once when the page mounts and forgotten after, so it reopens once: back
 * from the provider, not on every visit.
 */
export function useResumedSigning(recordType: string): PendingSigning | undefined {
  const [resumed] = useState(() => pendingSigning(recordType));
  useEffect(() => {
    if (resumed) {
      forgetSigning();
    }
  }, [resumed]);
  return resumed;
}
