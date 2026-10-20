import { useCallback, useState } from "react";
import { ApiError } from "@/api/client";
import { useSession } from "@/auth/session-context";

/** The duty names in one phrase, as the reader's language joins a list: "approval and receiving". */
export function dutyList(duties: readonly { name: string }[], language: string): string {
  return new Intl.ListFormat(language, { style: "long", type: "conjunction" }).format(duties.map((duty) => duty.name));
}

export interface ActingChoice {
  busy: boolean;
  /** Why the last choice failed: the API's problem, or nothing more known than that it failed. */
  failure: { error: ApiError | undefined } | undefined;
  /** Acts for this delegator, or as oneself with null. */
  choose: (delegatorId: string | null) => Promise<void>;
  clearFailure: () => void;
}

/** The one state the control and the band share: a choice under way, and why the last one failed. */
export function useActingChoice(): ActingChoice {
  const session = useSession();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ActingChoice["failure"]>();
  const choose = useCallback(
    async (delegatorId: string | null) => {
      setBusy(true);
      setFailure(undefined);
      try {
        await (delegatorId === null ? session.stopActing() : session.actFor(delegatorId));
      } catch (error) {
        setFailure({ error: error instanceof ApiError ? error : undefined });
      } finally {
        setBusy(false);
      }
    },
    [session],
  );
  const clearFailure = useCallback(() => setFailure(undefined), []);
  return { busy, failure, choose, clearFailure };
}
