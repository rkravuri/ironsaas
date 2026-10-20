import { useTranslation } from "react-i18next";
import { Alert, Stack, Text } from "@mantine/core";
import { IconPlayerPause } from "@tabler/icons-react";
import { useSession } from "@/auth/session-context";

/**
 * The kill-switch banner (TF-055, ADR-0035): for an administrator, one calm line per path the platform operator has
 * stopped, saying which, since when and why, with an icon beside the words. It sits in the page's flow at the top, so
 * it pushes the work down and never covers it. The API sends kill switches to administrators only, so the shell shows
 * what it is given and decides nothing.
 */
export function KillSwitchBanner() {
  const { t, i18n } = useTranslation();
  const { killSwitches } = useSession();
  if (killSwitches.length === 0) {
    return null;
  }

  const since = (iso: string) =>
    new Date(iso).toLocaleString(i18n.language, { dateStyle: "medium", timeStyle: "short" });
  return (
    <Stack gap="xs" mb="md">
      {killSwitches.map((killed) => (
        <Alert
          key={killed.flag}
          role="status"
          variant="light"
          color="warning"
          icon={<IconPlayerPause size={18} aria-hidden="true" />}
          title={t("killSwitch.title", { path: t(`killSwitch.paths.${killed.path}`, { defaultValue: killed.path }) })}
        >
          <Text size="sm">{t("killSwitch.since", { time: since(killed.since) })}</Text>
          <Text size="sm">
            {killed.reason ? t("killSwitch.reason", { reason: killed.reason }) : t("killSwitch.noReason")}
          </Text>
        </Alert>
      ))}
    </Stack>
  );
}
