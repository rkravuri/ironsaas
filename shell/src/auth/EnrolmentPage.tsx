import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Navigate, useLocation, useNavigate } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Button, Container, Group, Paper, Stack, Text, Title } from "@mantine/core";
import {
  addApp,
  addPasskey,
  addPasskeyOptions,
  confirmApp,
  factorsQueryKey,
  fetchFactors,
  type Enrolled,
} from "@/api/identity";
import { LoadingState } from "@/components/panels";
import { Welcome } from "@/design/illustrations";
import { TenantMark } from "@/design/patterns";
import type { EnrolmentState } from "@/shell/Guards";
import { FactorEnrolment, ProblemAlert, RecoveryCodesPanel } from "./mfa";
import { mfaProblem } from "./mfa-problems";
import { ReauthenticationDialog } from "./Reauthentication";
import { useSession } from "./session-context";

/**
 * Enrolment in the session (ADR-0047): the tenant's policy now requires a second factor of a person who is signed in and
 * has none, and every page waits for it. The person sets one up here, sees the first factor's recovery codes once, and
 * goes back to the page they were on, still signed in. Adding a factor asks for a recent sign-in, so the page carries
 * the re-authentication dialog the frame would. A person who needs no enrolment goes straight back.
 */
export function EnrolmentPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const session = useSession();
  const hostname = window.location.hostname;
  const from = (location.state as EnrolmentState | null)?.from ?? "/";
  const back = from.startsWith("/enrol") ? "/" : from;
  const [checked, setChecked] = useState(false);
  const [codes, setCodes] = useState<readonly string[] | undefined>();
  const [enrolled, setEnrolled] = useState(false);
  const factors = useQuery({ queryKey: factorsQueryKey, queryFn: ({ signal }) => fetchFactors(signal) });

  // /me as it is now: the frame may have come here on a call's refusal, before its own /me knew.
  useEffect(() => {
    void session.refresh().finally(() => setChecked(true));
    // Read once, when the page opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const finish = async () => {
    await session.refresh();
    void navigate(back, { replace: true });
  };

  const onEnrolled = (result: Enrolled) => {
    setEnrolled(true);
    if (result.recovery_codes && result.recovery_codes.length > 0) {
      setCodes(result.recovery_codes);
      return;
    }
    void finish();
  };

  if (checked && !enrolled && session.me && !session.me.mfa_enrolment_required) {
    return <Navigate to={back} replace />;
  }

  return (
    <Container size={560} py="xl" data-enrolment-required>
      <Stack align="center" gap="xs" mb="md">
        <Welcome decorative size="md" />
        <Group gap="sm" justify="center" wrap="nowrap">
          <TenantMark hostname={hostname} size={32} />
          <Title order={2} variant="display">
            {codes ? t("mfa.codes.title") : t("mfa.required.title")}
          </Title>
        </Group>
        <Text c="dimmed" size="sm" ta="center">
          {t("app.tenant", { hostname })}
        </Text>
      </Stack>
      <Paper withBorder shadow="sm" p="lg">
        {codes ? (
          <RecoveryCodesPanel codes={codes} account={session.me?.login_id ?? ""} onDone={() => void finish()} />
        ) : !checked || factors.isPending ? (
          <LoadingState height={160} />
        ) : factors.isError ? (
          <ProblemAlert problem={mfaProblem(factors.error, t)} />
        ) : (
          <Stack>
            <Text size="sm">{t("mfa.required.intro")}</Text>
            <FactorEnrolment
              allowed={factors.data.allowed}
              calls={{
                startApp: addApp,
                confirmApp,
                passkeyOptions: async () => (await addPasskeyOptions()).options,
                registerPasskey: addPasskey,
              }}
              onEnrolled={onEnrolled}
            />
          </Stack>
        )}
      </Paper>
      {!codes && (
        <Group justify="center" mt="md">
          <Button variant="subtle" size="compact-sm" onClick={() => void session.signOut()}>
            {t("mfa.required.signOut")}
          </Button>
        </Group>
      )}
      <ReauthenticationDialog />
    </Container>
  );
}
