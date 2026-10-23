import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Group, Stack, Text } from "@mantine/core";
import { IconUserCircle } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { useSession } from "@/auth/session-context";
import { formatDateTime } from "@/i18n/dates";

const until = (iso: string) => formatDateTime(iso);

/**
 * The band an impersonator sees on every page (TF-025, ADR-0049): whose account this is, until when, why, and End. It sits
 * in the page's flow, so it never covers the work, and it says what an impersonator may never do here. After the end,
 * the same place says the impersonator is themselves again.
 */
export function ImpersonationBand() {
  const { t } = useTranslation();
  const { me, impersonationEnded, endImpersonation, dismissImpersonationEnded } = useSession();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();
  const impersonation = me?.impersonation ?? null;

  const end = async () => {
    setBusy(true);
    setFailure(undefined);
    try {
      await endImpersonation();
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      {impersonation && me && (
        <Alert
          role="status"
          variant="light"
          color="warning"
          mb="md"
          icon={<IconUserCircle size={18} aria-hidden="true" />}
          title={t("impersonation.band.title", { name: me.display_name })}
          data-impersonation-band
        >
          <Group justify="space-between" align="flex-end" gap="sm">
            <Stack gap={2} maw={720}>
              <Text size="sm">
                {t(impersonation.operator ? "impersonation.band.byOperator" : "impersonation.band.by", {
                  impersonator: impersonation.impersonator_name,
                  until: until(impersonation.until),
                })}
              </Text>
              <Text size="sm">{t("impersonation.band.reason", { reason: impersonation.reason })}</Text>
              <Text size="sm">{t("impersonation.band.never")}</Text>
            </Stack>
            <Button variant="default" size="xs" loading={busy} onClick={() => void end()}>
              {t("impersonation.band.end")}
            </Button>
          </Group>
          {failure && (
            <Text size="sm" mt="xs" role="alert">
              {failure.message}
            </Text>
          )}
        </Alert>
      )}
      {impersonationEnded && !impersonation && (
        <Alert
          role="status"
          variant="light"
          color="info"
          mb="md"
          withCloseButton
          closeButtonLabel={t("dialog.close")}
          onClose={dismissImpersonationEnded}
          data-impersonation-ended
        >
          <Text size="sm">{t("impersonation.ended")}</Text>
        </Alert>
      )}
    </>
  );
}
