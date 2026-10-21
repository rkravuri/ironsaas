import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Divider, Group, Stack, Text, TextInput, Title } from "@mantine/core";
import { IconFingerprint } from "@tabler/icons-react";
import {
  proveSecondFactor,
  secondFactorPasskeyOptions,
  type SecondFactorProof,
  type SignInAnswer,
  type SignInStep,
} from "@/api/identity";
import { CodeInput, ProblemAlert } from "./mfa";
import { mfaProblem, signInEnded, type ShownProblem } from "./mfa-problems";
import { passkeysSupported, signWithPasskey } from "./webauthn";

/**
 * The second step of a sign-in (TF-013, ADR-0047): after a right password, the person's passkey, the code from their
 * app, or one of their recovery codes. The password made no session; this step does. A wrong code says only that it did
 * not prove the person; after three in a row the page says how long to wait. When the sign-in has ended (five minutes,
 * or a lock) the person starts again from the password.
 */
export function SecondFactorStep({
  step,
  onSignedIn,
  onRestart,
}: {
  step: SignInStep;
  onSignedIn: (answer: SignInAnswer) => void;
  onRestart: () => void;
}) {
  const { t } = useTranslation();
  const hasApp = step.methods.includes("authenticator_app");
  const hasPasskey = step.methods.includes("passkey") && passkeysSupported();
  const hasRecovery = step.methods.includes("recovery_code");
  const [mode, setMode] = useState<"code" | "recovery">(hasApp ? "code" : "recovery");
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState<"code" | "passkey" | undefined>();
  const [problem, setProblem] = useState<ShownProblem | undefined>();
  const [ended, setEnded] = useState(false);

  const prove = async (proof: () => Promise<SecondFactorProof>, kind: "code" | "passkey") => {
    setBusy(kind);
    setProblem(undefined);
    try {
      onSignedIn(await proveSecondFactor(await proof()));
    } catch (error) {
      setProblem(mfaProblem(error, t));
      setEnded(signInEnded(error));
    } finally {
      setBusy(undefined);
    }
  };

  const withPasskey = () =>
    void prove(
      async () => ({ passkey: await signWithPasskey((await secondFactorPasskeyOptions()).options) }),
      "passkey",
    );

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    const typed = code.trim();
    void prove(() => Promise.resolve(mode === "code" ? { code: typed } : { recovery_code: typed }), "code");
  };

  return (
    <Stack data-second-factor>
      <Stack gap={4}>
        <Title order={3} size="h4">
          {t("mfa.second.title")}
        </Title>
        <Text size="sm" c="dimmed">
          {t("mfa.second.intro")}
        </Text>
      </Stack>
      {problem && (
        <ProblemAlert problem={problem}>
          {ended && (
            <Group mt="sm">
              <Button size="xs" variant="default" onClick={onRestart}>
                {t("mfa.second.restart")}
              </Button>
            </Group>
          )}
        </ProblemAlert>
      )}
      {!ended && (
        <>
          {hasPasskey && (
            <Button
              onClick={withPasskey}
              loading={busy === "passkey"}
              disabled={busy !== undefined}
              leftSection={<IconFingerprint size={18} aria-hidden="true" />}
              data-use-passkey
            >
              {t("mfa.second.passkey")}
            </Button>
          )}
          {hasPasskey && (hasApp || hasRecovery) && <Divider label={t("mfa.second.or")} labelPosition="center" />}
          {(hasApp || hasRecovery) && (
            <form onSubmit={submit} noValidate>
              <Stack>
                {mode === "code" ? (
                  <CodeInput
                    label={t("mfa.second.code")}
                    description={t("mfa.second.codeHint")}
                    value={code}
                    onChange={setCode}
                    autoFocus={!hasPasskey}
                  />
                ) : (
                  <TextInput
                    label={t("mfa.second.recovery")}
                    description={t("mfa.second.recoveryHint")}
                    value={code}
                    onChange={(event) => setCode(event.currentTarget.value)}
                    autoComplete="off"
                    spellCheck={false}
                    required
                    styles={{ input: { fontFamily: "var(--iron-font-mono)" } }}
                  />
                )}
                <Button
                  type="submit"
                  variant={hasPasskey ? "default" : "filled"}
                  loading={busy === "code"}
                  disabled={busy !== undefined || code.trim().length === 0}
                >
                  {t("mfa.second.submit")}
                </Button>
                {hasApp && hasRecovery && (
                  <Button
                    variant="subtle"
                    size="compact-sm"
                    onClick={() => {
                      setMode(mode === "code" ? "recovery" : "code");
                      setCode("");
                      setProblem(undefined);
                    }}
                  >
                    {mode === "code" ? t("mfa.second.useRecovery") : t("mfa.second.useCode")}
                  </Button>
                )}
              </Stack>
            </form>
          )}
          <Button variant="subtle" size="compact-sm" onClick={onRestart}>
            {t("mfa.second.another")}
          </Button>
        </>
      )}
    </Stack>
  );
}
