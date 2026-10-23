import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Alert, Anchor, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { fetchSecurityAlerts } from "@/api/identity";
import { useSession } from "@/auth/session-context";
import { formatDateTime } from "@/i18n/dates";

const manage = "identity.users.manage";
const dismissedKey = "iron.emergency-alerts.dismissed";

/** The sign-ins this browser was told about already: a per-browser convenience, never needed for the alert to exist. */
function readDismissed(): string[] {
  try {
    const stored = window.localStorage.getItem(dismissedKey);
    return stored ? (JSON.parse(stored) as string[]) : [];
  } catch {
    return [];
  }
}

function writeDismissed(ids: string[]): void {
  try {
    window.localStorage.setItem(dismissedKey, JSON.stringify(ids.slice(-50)));
  } catch {
    // Storage may be off in this browser: the banner comes back on the next page, which is the safe side.
  }
}

/**
 * The alert of an emergency account's sign-in (ADR-0049), for the tenant's administrators: who signed in and when, in the
 * last 24 hours, read every minute. The same sign-in is a security event of the person's and an audit event at high
 * severity; this banner is where an administrator sees it at once. Closing it hides those sign-ins in this browser.
 */
export function EmergencyBanner() {
  const { t } = useTranslation();
  const session = useSession();
  const [dismissed, setDismissed] = useState<string[]>(readDismissed);
  const allowed = session.tenantPermissions.includes(manage);
  const alerts = useQuery({
    queryKey: ["identity", "security-alerts"],
    queryFn: ({ signal }) => fetchSecurityAlerts(signal),
    enabled: allowed,
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
  });
  const fresh = (alerts.data?.emergency_sign_ins ?? []).filter((alert) => !dismissed.includes(alert.id));
  if (!allowed || fresh.length === 0) {
    return null;
  }

  const at = (iso: string) => formatDateTime(iso);
  const dismiss = () => {
    const ids = [...dismissed, ...fresh.map((alert) => alert.id)];
    writeDismissed(ids);
    setDismissed(ids);
  };
  return (
    <Alert
      role="alert"
      variant="light"
      color="danger"
      mb="md"
      icon={<IconAlertTriangle size={18} aria-hidden="true" />}
      title={t("emergency.banner.title")}
      withCloseButton
      closeButtonLabel={t("emergency.banner.dismiss")}
      onClose={dismiss}
      data-emergency-banner
    >
      <Stack gap={2}>
        {fresh.map((alert) => (
          <Text size="sm" key={alert.id}>
            {t("emergency.banner.signedIn", { name: alert.display_name, login: alert.login_id, at: at(alert.at) })}{" "}
            <Anchor
              component={Link}
              to={`/users/${alert.principal_id}`}
              size="sm"
              c="inherit"
              underline="always"
              fw={600}
            >
              {t("emergency.banner.open")}
            </Anchor>
          </Text>
        ))}
        <Text size="sm">{t("emergency.banner.explain")}</Text>
      </Stack>
    </Alert>
  );
}
