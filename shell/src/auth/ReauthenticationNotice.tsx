import { useTranslation } from "react-i18next";
import { useLocation, useNavigate } from "react-router";
import { Alert, Text } from "@mantine/core";
import type { ShownProblem } from "./mfa-problems";

/** How a confirmation through a company login came back to the page (TF-248 AC6): confirmed, or why not. */
export interface ReauthenticationState {
  reauthentication?: { confirmed: true } | { problem: ShownProblem };
}

/**
 * The page a person left to confirm it is them through their company login says how it went (TF-248 AC6, ADR-0048):
 * confirmed, and the action is ready to take again; or not, with why, and the action was not done. Closing it forgets it.
 */
export function ReauthenticationNotice() {
  const { t } = useTranslation();
  const location = useLocation();
  const navigate = useNavigate();
  const outcome = (location.state as ReauthenticationState | null)?.reauthentication;
  if (outcome === undefined) {
    return null;
  }
  const close = () => void navigate(location.pathname + location.search, { replace: true, state: null });
  return "confirmed" in outcome ? (
    <Alert
      color="success"
      role="status"
      mb="md"
      withCloseButton
      onClose={close}
      closeButtonLabel={t("dialog.close")}
      data-reauthentication-confirmed
    >
      {t("mfa.reauth.sso.confirmed")}
    </Alert>
  ) : (
    <Alert
      color="danger"
      role="alert"
      mb="md"
      withCloseButton
      onClose={close}
      closeButtonLabel={t("dialog.close")}
      data-reauthentication-refused
    >
      {outcome.problem.message}
      {outcome.problem.correlationId !== undefined && (
        <Text size="xs" c="dimmed" mt={4}>
          {t("errors.correlation", { id: outcome.problem.correlationId })}
        </Text>
      )}
    </Alert>
  );
}
