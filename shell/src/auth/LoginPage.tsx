import { useEffect, useRef, useState } from "react";
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
import { IconBuildingSkyscraper, IconFingerprint } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import {
  completeSso,
  confirmEnrolmentApp,
  discoverProvider,
  enrolmentApp,
  enrolmentPasskey,
  enrolmentPasskeyOptions,
  fetchSignInOptions,
  passkeySignIn,
  passkeySignInOptions,
  signIn,
  ssoSignOutUrl,
  startSso,
  type SignInAnswer,
  type SignInStep,
  type SsoProviderButton,
} from "@/api/identity";
import type { RecoveryState } from "./RecoveryPage";
import { Welcome } from "@/design/illustrations";
import { TenantMark } from "@/design/patterns";
import { LoadingState } from "@/components/panels";
import type { LoginState } from "@/shell/Guards";
import { leaveFor } from "./leave";
import type { ReauthenticationState } from "./ReauthenticationNotice";
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
  /** The page the company login sends the browser back to, with what it brought in the address (ADR-0048). */
  callback?: boolean;
}

/** What the login page is told when it opens: why a session ended, where to go, and a company login's refusal. */
interface LoginPageState extends LoginState {
  problem?: ShownProblem;
}

type Stage =
  | { kind: "password" }
  | { kind: "callback" }
  | { kind: "second_factor"; step: SignInStep }
  | { kind: "enrolment"; step: SignInStep }
  | { kind: "codes"; answer: SignInAnswer; codes: readonly string[] };

const looksLikeEmail = (value: string) => /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(value.trim());

/**
 * Signing in (TF-011, TF-013, TF-014, ADR-0046, ADR-0047, ADR-0048). One fixed message for every failed password, the
 * API's own (TF-011 AC2): the page never says whether the account exists, which of the two was wrong, or whether the
 * account is active. A login locked after too many failures has its own message, the same for every identifier. A right
 * password goes on to the second step when the person has a factor, or to enrolment when the policy requires one and
 * they have none: no page opens before either. The first enrolment shows the recovery codes once, and the page waits
 * until the person says they saved them. A sign-in by recovery code goes on to My profile to add a new factor. A right
 * password past its maximum age goes on to the recovery page with the code the API gave. Where the tenant allows it, a
 * passkey signs in alone. Until email exists (S4), "Forgot your password?" says to ask the administrator for a recovery
 * link.
 *
 * A tenant's company logins have a button each; typing a work email without a password sends the person to the one of
 * its domain. A person who signs in only through one is told "Use your company login", with its button. The page the
 * company login sends the browser back to is this one too: it completes the sign-in, and the second step, enrolment and
 * the recovery codes follow as after a password.
 */
export function LoginPage({ autoFocus = true, callback = false }: LoginPageProps = {}) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const session = useSession();
  const state = location.state as LoginPageState | null;
  const [stage, setStage] = useState<Stage>(callback ? { kind: "callback" } : { kind: "password" });
  const [problem, setProblem] = useState<ShownProblem | undefined>(state?.problem);
  const [companyLogin, setCompanyLogin] = useState<SsoProviderButton | undefined>();
  const [submitting, setSubmitting] = useState<"password" | "passkey" | "provider" | undefined>();
  const [signedOutThere, setSignedOutThere] = useState<"idle" | "leaving" | "nothing">("idle");
  const options = useQuery({
    queryKey: ["auth", "sign-in-options"],
    queryFn: ({ signal }) => fetchSignInOptions(signal),
    retry: false,
    enabled: !callback,
  });
  const form = useForm<LoginFormValues>({
    initialValues: { loginId: "", password: "" },
    validate: {
      loginId: (value) => (value.trim().length === 0 ? t("login.required") : null),
    },
  });
  const hostname = window.location.hostname;
  const providers = options.data?.providers ?? [];

  const returnTo = state?.from ?? "/";
  const reason = state?.reason;

  const complete = async (answer: SignInAnswer) => {
    // Held while this page decides where the person goes: My profile after a recovery code, else where they were going.
    holdSignIn(true);
    try {
      await session.signedIn();
      void navigate(answer.enrol_new_factor ? enrolNewFactorPath : (answer.return_to ?? returnTo), { replace: true });
    } finally {
      holdSignIn(false);
    }
  };

  /** A session or the next step: the recovery codes of a first enrolment are shown first, once. */
  const proceed = async (answer: SignInAnswer) => {
    if (answer.reauthenticated) {
      // The session's person confirmed it is them through their company login: back to their page, the action ready.
      const back: ReauthenticationState = { reauthentication: { confirmed: true } };
      void navigate(answer.return_to ?? "/", { replace: true, state: back });
      return;
    }
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

  /** Sends the browser to a company login, which sends it back to /sso/callback. */
  const continueWith = async (provider: SsoProviderButton, loginHint?: string) => {
    setSubmitting("provider");
    setProblem(undefined);
    try {
      const started = await startSso(provider.id, returnTo, loginHint);
      leaveFor(started.authorization_url);
    } catch (error) {
      setProblem(mfaProblem(error, t));
      setSubmitting(undefined);
    }
  };

  /**
   * The password step's refusal: one fixed message for every failure (TF-011 AC2), whatever the API said, except a
   * locked login, whose message is the same for every identifier, and a person who signs in only through a company
   * login (TF-014 AC2); an expired password goes to the recovery page.
   */
  const passwordRefused = (error: unknown): void => {
    if (error instanceof ApiError && error.errorCode === "identity.password.expired" && error.recoveryToken) {
      refused(error);
      return;
    }
    if (error instanceof ApiError && error.errorCode === "identity.login.use_company_login") {
      setCompanyLogin(error.provider);
      setProblem({ message: t("login.useCompanyLogin"), correlationId: error.correlationId });
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
    setCompanyLogin(undefined);
    try {
      if (values.password.length === 0) {
        // A work email and no password: its domain's company login, when the tenant has one (TF-014, ADR-0048).
        const provider =
          providers.length > 0 && looksLikeEmail(values.loginId)
            ? (await discoverProvider(values.loginId.trim())).provider
            : null;
        if (provider) {
          await continueWith(provider, values.loginId.trim());
          return;
        }
        form.setFieldError("password", providers.length > 0 ? t("login.passwordOrCompany") : t("login.required"));
        return;
      }
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
      setSubmitting((current) => (current === "provider" ? current : undefined));
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

  // The company login sent the browser back: what it brought is read from the address once, which is then cleared, and
  // completes the sign-in. A refusal goes to the sign-in page with its words.
  const completing = useRef(false);
  useEffect(() => {
    if (!callback || completing.current) {
      return;
    }
    completing.current = true;
    const query = new URLSearchParams(location.search);
    void navigate(location.pathname, { replace: true });
    completeSso({
      code: query.get("code"),
      state: query.get("state"),
      error: query.get("error"),
      error_description: query.get("error_description"),
    })
      .then(proceed)
      .catch((error: unknown) => {
        const shown = signInEnded(error)
          ? { message: t("login.sso.ended"), correlationId: undefined }
          : mfaProblem(error, t);
        // A refused re-authentication goes back to the page the person was on, which says why; the session stays.
        if (
          error instanceof ApiError &&
          (error.returnTo !== undefined || (error.status === 403 && error.errorCode === "identity.sign_in.expired"))
        ) {
          // The API's own words: they name the company login and say the action was not done.
          const back: ReauthenticationState = {
            reauthentication: { problem: { message: error.message, correlationId: error.correlationId } },
          };
          void navigate(error.returnTo ?? "/", { replace: true, state: back });
          return;
        }
        // The sign-in page is this page under another address: React keeps it, so it changes its own stage.
        setProblem(shown);
        setStage({ kind: "password" });
        void navigate("/login", { replace: true, state: { problem: shown } satisfies LoginPageState });
      });
    // Once, with the address the company login sent the browser back to.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const signOutThere = async (provider: SsoProviderButton) => {
    setSignedOutThere("leaving");
    try {
      leaveFor((await ssoSignOutUrl(provider.id)).url);
    } catch {
      setSignedOutThere("nothing");
    }
  };

  const passkeyOffered = options.data?.passkey_sign_in === true && passkeysSupported();
  const signedOutOf = reason === "signed-out" ? session.signedOutOf : undefined;

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
          {signedOutOf && (
            <Stack gap="xs" mt="xs" data-sign-out-there>
              <Text size="sm">{t("login.sso.signOutThereText", { name: signedOutOf.name })}</Text>
              {signedOutThere === "nothing" ? (
                <Text size="sm">{t("login.sso.nothingThere", { name: signedOutOf.name })}</Text>
              ) : (
                <Group>
                  <Button
                    variant="default"
                    size="compact-sm"
                    loading={signedOutThere === "leaving"}
                    onClick={() => void signOutThere(signedOutOf)}
                  >
                    {t("login.sso.signOutThere", { name: signedOutOf.name })}
                  </Button>
                </Group>
              )}
            </Stack>
          )}
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
          {stage.kind === "callback" && (
            <Stack gap="xs" data-sso-callback>
              <LoadingState height={120} />
              <Text size="sm" ta="center" role="status">
                {t("login.sso.completing")}
              </Text>
            </Stack>
          )}
          {stage.kind === "password" && (
            <form onSubmit={submit} noValidate>
              <Stack>
                {providers.length > 0 && (
                  <>
                    <Stack gap="xs" data-company-logins>
                      {providers.map((provider) => (
                        <Button
                          key={provider.id}
                          variant="default"
                          fullWidth
                          leftSection={<IconBuildingSkyscraper size={18} aria-hidden="true" />}
                          loading={submitting === "provider"}
                          disabled={submitting !== undefined && submitting !== "provider"}
                          onClick={() => void continueWith(provider)}
                        >
                          {t("login.sso.continueWith", { name: provider.name })}
                        </Button>
                      ))}
                    </Stack>
                    <Divider label={t("mfa.second.or")} labelPosition="center" />
                  </>
                )}
                <TextInput
                  label={t("login.loginId")}
                  {...(providers.length > 0 ? { description: t("login.sso.emailHint") } : {})}
                  autoComplete="username webauthn"
                  autoFocus={autoFocus}
                  required
                  {...form.getInputProps("loginId")}
                />
                <PasswordInput
                  label={t("login.password")}
                  autoComplete="current-password"
                  required={providers.length === 0}
                  {...form.getInputProps("password")}
                />
                {problem && <ProblemAlert problem={problem} />}
                {companyLogin && (
                  <Button
                    variant="light"
                    fullWidth
                    leftSection={<IconBuildingSkyscraper size={18} aria-hidden="true" />}
                    loading={submitting === "provider"}
                    onClick={() => void continueWith(companyLogin, form.values.loginId.trim())}
                    data-use-company-login
                  >
                    {t("login.sso.continueWith", { name: companyLogin.name })}
                  </Button>
                )}
                <Button
                  type="submit"
                  loading={submitting === "password"}
                  disabled={submitting === "passkey" || submitting === "provider"}
                  fullWidth
                >
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
                      disabled={submitting === "password" || submitting === "provider"}
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
              account={form.values.loginId.trim() || session.me?.login_id || ""}
              onDone={() => void complete(stage.answer)}
            />
          )}
        </Paper>
      </Box>
    </Container>
  );
}
