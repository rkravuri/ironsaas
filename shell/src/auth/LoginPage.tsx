import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useLocation, useNavigate } from "react-router";
import { useQuery } from "@tanstack/react-query";
import {
  Alert,
  Box,
  Button,
  Container,
  Divider,
  Group,
  Paper,
  PasswordInput,
  Stack,
  Text,
  TextInput,
  Title,
} from "@mantine/core";
import { useForm } from "@mantine/form";
import { IconFingerprint } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import {
  confirmEnrolmentApp,
  enrolmentApp,
  enrolmentPasskey,
  enrolmentPasskeyOptions,
  fetchSignInOptions,
  passkeySignIn,
  passkeySignInOptions,
  signIn,
  type SignInAnswer,
  type SignInStep,
} from "@/api/identity";
import type { RecoveryState } from "./RecoveryPage";
import { Welcome } from "@/design/illustrations";
import { TenantMark } from "@/design/patterns";
import type { LoginState } from "@/shell/Guards";
import { FactorEnrolment, ProblemAlert, RecoveryCodesPanel } from "./mfa";
import { enrolNewFactorPath, mfaProblem, signInEnded, type ShownProblem } from "./mfa-problems";
import { SecondFactorStep } from "./SecondFactorStep";
import { useSession } from "./session-context";
import { holdSignIn } from "./sign-in-hold";
import { passkeysSupported, signWithPasskey } from "./webauthn";

interface LoginFormValues {
  loginId: string;
  password: string;
}

interface LoginPageProps {
  /** Off in the gallery, where a focused field would steal the page. */
  autoFocus?: boolean;
}

type Stage =
  | { kind: "password" }
  | { kind: "second_factor"; step: SignInStep }
  | { kind: "enrolment"; step: SignInStep }
  | { kind: "codes"; answer: SignInAnswer; codes: readonly string[] };

/**
 * Signing in (TF-011, TF-013, ADR-0046, ADR-0047). One fixed message for every failed password, the API's own (TF-011
 * AC2): the page never says whether the account exists, which of the two was wrong, or whether the account is active. A
 * login locked after too many failures has its own message, the same for every identifier. A right password goes on to
 * the second step when the person has a factor, or to enrolment when the policy requires one and they have none: no page
 * opens before either. The first enrolment shows the recovery codes once, and the page waits until the person says they
 * saved them. A sign-in by recovery code goes on to My profile to add a new factor. A right password past its maximum age
 * goes on to the recovery page with the code the API gave. Where the tenant allows it, a passkey signs in alone. Until
 * email exists (S4), "Forgot your password?" says to ask the administrator for a recovery link.
 */
export function LoginPage({ autoFocus = true }: LoginPageProps = {}) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const session = useSession();
  const [stage, setStage] = useState<Stage>({ kind: "password" });
  const [problem, setProblem] = useState<ShownProblem | undefined>();
  const [submitting, setSubmitting] = useState<"password" | "passkey" | undefined>();
  const options = useQuery({
    queryKey: ["auth", "sign-in-options"],
    queryFn: ({ signal }) => fetchSignInOptions(signal),
    retry: false,
  });
  const form = useForm<LoginFormValues>({
    initialValues: { loginId: "", password: "" },
    validate: {
      loginId: (value) => (value.trim().length === 0 ? t("login.required") : null),
      password: (value) => (value.length === 0 ? t("login.required") : null),
    },
  });
  const hostname = window.location.hostname;

  const state = location.state as LoginState | null;
  const returnTo = state?.from ?? "/";
  const reason = state?.reason;

  const complete = async (answer: SignInAnswer) => {
    // Held while this page decides where the person goes: My profile after a recovery code, else where they were going.
    holdSignIn(true);
    try {
      await session.signedIn();
      void navigate(answer.enrol_new_factor ? enrolNewFactorPath : returnTo, { replace: true });
    } finally {
      holdSignIn(false);
    }
  };

  /** A session or the next step: the recovery codes of a first enrolment are shown first, once. */
  const proceed = async (answer: SignInAnswer) => {
    if (answer.step) {
      setStage({ kind: answer.step.kind, step: answer.step });
      return;
    }
    if (answer.recovery_codes && answer.recovery_codes.length > 0) {
      holdSignIn(true);
      setStage({ kind: "codes", answer, codes: answer.recovery_codes });
      return;
    }
    await complete(answer);
  };

  /** A refusal of any step: an expired password goes to the recovery page; anything else is said here. */
  const refused = (error: unknown): void => {
    if (error instanceof ApiError && error.errorCode === "identity.password.expired" && error.recoveryToken) {
      const recovery: RecoveryState = { expired: true };
      void navigate(`/recovery#${error.recoveryToken}`, { state: recovery });
      return;
    }
    setProblem(mfaProblem(error, t));
  };

  /**
   * The password step's refusal: one fixed message for every failure (TF-011 AC2), whatever the API said, except a
   * locked login, whose message is the same for every identifier; an expired password goes to the recovery page.
   */
  const passwordRefused = (error: unknown): void => {
    if (error instanceof ApiError && error.errorCode === "identity.password.expired" && error.recoveryToken) {
      refused(error);
      return;
    }
    setProblem({
      message:
        error instanceof ApiError && error.errorCode === "identity.login.locked"
          ? t("login.locked")
          : t("login.failed"),
      correlationId: error instanceof ApiError ? error.correlationId : undefined,
    });
  };

  const restart = (shown?: ShownProblem) => {
    holdSignIn(false);
    form.setFieldValue("password", "");
    setProblem(shown);
    setStage({ kind: "password" });
  };

  const submit = form.onSubmit(async (values) => {
    setSubmitting("password");
    setProblem(undefined);
    try {
      let answer: SignInAnswer;
      try {
        answer = await signIn(values.loginId.trim(), values.password);
      } catch (error) {
        passwordRefused(error);
        return;
      }
      await proceed(answer);
    } catch (error) {
      refused(error);
    } finally {
      setSubmitting(undefined);
    }
  });

  const withPasskey = async () => {
    setSubmitting("passkey");
    setProblem(undefined);
    try {
      const credential = await signWithPasskey((await passkeySignInOptions()).options);
      await proceed(await passkeySignIn(credential));
    } catch (error) {
      refused(error);
    } finally {
      setSubmitting(undefined);
    }
  };

  const passkeyOffered = options.data?.passkey_sign_in === true && passkeysSupported();

  return (
    <Container size={stage.kind === "password" || stage.kind === "second_factor" ? 420 : 560} py="xl">
      <Stack align="center" gap="xs" mb="md">
        <Welcome decorative size="md" />
        <Group gap="sm" justify="center" wrap="nowrap">
          <TenantMark hostname={hostname} size={32} />
          <Title order={2} variant="display">
            {stage.kind === "enrolment"
              ? t("mfa.enrol.title")
              : stage.kind === "codes"
                ? t("mfa.codes.title")
                : t("login.title")}
          </Title>
        </Group>
        <Text c="dimmed" size="sm" ta="center">
          {t("app.tenant", { hostname })}
        </Text>
      </Stack>
      {reason !== undefined && stage.kind === "password" && (
        <Alert color="info" role="status" mb="md" data-reason={reason}>
          {t(`login.ended.${reason}`)}
        </Alert>
      )}
      {/* The mark as a texture: behind the opaque card at 7 percent, so it never sits under text (brief section 4). */}
      <Box pos="relative">
        <TenantMark
          hostname={hostname}
          size={400}
          opacity={0.07}
          style={{ position: "absolute", left: "50%", top: 0, transform: "translateX(-50%)", pointerEvents: "none" }}
        />
        <Paper withBorder shadow="sm" p="lg" pos="relative">
          {stage.kind === "password" && (
            <form onSubmit={submit} noValidate>
              <Stack>
                <TextInput
                  label={t("login.loginId")}
                  autoComplete="username webauthn"
                  autoFocus={autoFocus}
                  required
                  {...form.getInputProps("loginId")}
                />
                <PasswordInput
                  label={t("login.password")}
                  autoComplete="current-password"
                  required
                  {...form.getInputProps("password")}
                />
                {problem && <ProblemAlert problem={problem} />}
                <Button type="submit" loading={submitting === "password"} disabled={submitting === "passkey"} fullWidth>
                  {t("login.submit")}
                </Button>
                {passkeyOffered && (
                  <>
                    <Divider label={t("mfa.second.or")} labelPosition="center" />
                    <Button
                      variant="default"
                      fullWidth
                      onClick={() => void withPasskey()}
                      loading={submitting === "passkey"}
                      disabled={submitting === "password"}
                      leftSection={<IconFingerprint size={18} aria-hidden="true" />}
                      data-passkey-sign-in
                    >
                      {t("login.passkey")}
                    </Button>
                  </>
                )}
                <Text size="sm" data-forgot>
                  <Text span inherit fw={600}>
                    {t("login.forgot.title")}
                  </Text>{" "}
                  <Text span inherit c="dimmed">
                    {t("login.forgot.text")}
                  </Text>
                </Text>
              </Stack>
            </form>
          )}
          {stage.kind === "second_factor" && (
            <SecondFactorStep
              step={stage.step}
              onSignedIn={(answer) => void proceed(answer).catch(refused)}
              onRestart={() => restart()}
            />
          )}
          {stage.kind === "enrolment" && (
            <Stack data-enrolment>
              <Text size="sm">{t("mfa.enrol.intro")}</Text>
              <FactorEnrolment
                allowed={stage.step.methods}
                calls={{
                  startApp: enrolmentApp,
                  confirmApp: confirmEnrolmentApp,
                  passkeyOptions: async () => (await enrolmentPasskeyOptions()).options,
                  registerPasskey: enrolmentPasskey,
                }}
                onEnrolled={(answer) => void proceed(answer).catch(refused)}
                onProblem={(error) => {
                  if (!signInEnded(error)) {
                    return false;
                  }
                  restart(mfaProblem(error, t));
                  return true;
                }}
              />
            </Stack>
          )}
          {stage.kind === "codes" && (
            <RecoveryCodesPanel
              codes={stage.codes}
              account={form.values.loginId.trim()}
              onDone={() => void complete(stage.answer)}
            />
          )}
        </Paper>
      </Box>
    </Container>
  );
}
