import { useEffect, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Text } from "@mantine/core";
import { ApiError } from "@/api/client";
import { AlertDialog } from "@/components/panels";
import { nextChangeIn, phaseAt } from "./session-clock";
import { useSession } from "./session-context";
import { useSessionWatch } from "./session-watch";

/** A browser runs one timer at most this far ahead; a longer wait is taken in steps. */
const longestTimer = 2_147_483_647;

const clockTime = (at: number) => new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" }).format(at);

interface Failure {
  message: string;
  correlationId: string | undefined;
}

/**
 * The session warning (TF-021, WCAG 2.2.1, ADR-0033). Two minutes before the idle end an alert dialog takes focus and
 * offers to stay signed in, one request that touches the session; near the absolute limit it says that nothing can
 * extend the session. Another tab's activity closes it here; when the clock runs out, every tab signs out.
 */
export function SessionWarning() {
  const { t } = useTranslation();
  const { extend, signOut, expire } = useSession();
  const watch = useSessionWatch();
  const times = useSyncExternalStore(watch.subscribe, watch.getTimes);
  const [now, setNow] = useState(() => Date.now());
  const [extending, setExtending] = useState(false);
  const [failure, setFailure] = useState<Failure | undefined>();
  const [dismissed, setDismissed] = useState<string | undefined>();

  // One timer, for the next moment the phase can change; nothing ticks in between.
  useEffect(() => {
    if (times === undefined) {
      return undefined;
    }
    const wait = nextChangeIn(times, Date.now());
    if (wait === undefined) {
      return undefined;
    }
    const timer = window.setTimeout(() => setNow(Date.now()), Math.min(wait, longestTimer));
    return () => window.clearTimeout(timer);
  }, [times, now]);

  // Timers sleep in a hidden tab and on a closed lid: a tab that comes back reads the clock again.
  useEffect(() => {
    const wake = () => setNow(Date.now());
    document.addEventListener("visibilitychange", wake);
    window.addEventListener("focus", wake);
    return () => {
      document.removeEventListener("visibilitychange", wake);
      window.removeEventListener("focus", wake);
    };
  }, []);

  const phase = times === undefined ? undefined : phaseAt(times, now);
  const endedFor = phase?.kind === "ended" ? phase.reason : undefined;
  useEffect(() => {
    if (endedFor !== undefined) {
      expire(endedFor);
    }
  }, [endedFor, expire]);

  const stay = async () => {
    setExtending(true);
    setFailure(undefined);
    try {
      await extend();
    } catch (error) {
      // A 401 has already sent every tab to the login page; anything else stays here, with its correlation id.
      if (!(error instanceof ApiError && error.isUnauthenticated)) {
        setFailure(error instanceof ApiError ? { message: error.message, correlationId: error.correlationId } : { message: t("errors.unexpected"), correlationId: undefined });
      }
    } finally {
      setExtending(false);
    }
  };

  const warning = phase?.kind === "warning";
  const final = phase?.kind === "final" && dismissed !== times?.sessionId;
  const at = phase?.kind === "warning" || phase?.kind === "final" ? clockTime(phase.endsAt) : "";

  return (
    <AlertDialog
      opened={warning || final}
      title={final ? t("session.final.title") : t("session.warning.title")}
      message={final ? t("session.final.message", { time: at }) : t("session.warning.message", { time: at })}
      actions={
        <>
          <Button variant="default" onClick={() => void signOut()}>
            {t("app.signOut")}
          </Button>
          {final ? (
            <Button data-autofocus onClick={() => setDismissed(times?.sessionId)}>
              {t("session.final.keepWorking")}
            </Button>
          ) : (
            <Button data-autofocus loading={extending} onClick={() => void stay()}>
              {t("session.warning.stay")}
            </Button>
          )}
        </>
      }
    >
      {failure && (
        <Alert color="danger" role="alert">
          {failure.message}
          {failure.correlationId !== undefined && (
            <Text size="xs" c="dimmed" mt={4}>
              {t("errors.correlation", { id: failure.correlationId })}
            </Text>
          )}
        </Alert>
      )}
    </AlertDialog>
  );
}
