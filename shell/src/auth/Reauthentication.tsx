import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Divider, Group, PasswordInput, Stack, Text, TextInput } from "@mantine/core";
import { IconFingerprint } from "@tabler/icons-react";
import { onReauthenticationRequired } from "@/api/client";
import {
  reauthenticate,
  reauthenticationOptions,
  startSso,
  type Reauthentication,
  type SecondFactorProof,
} from "@/api/identity";
import { leaveFor } from "./leave";
import { FormDialog } from "@/components/panels";
import { CodeInput, ProblemAlert } from "./mfa";
import { mfaProblem, type ShownProblem } from "./mfa-problems";
import { passkeysSupported, signWithPasskey } from "./webauthn";

/**
 * The shell's one re-authentication dialog (TF-248, ADR-0047). A high-risk action the API refuses for an old sign-in
 * opens it, from any page; the person confirms it is them with their password and, when they have one, their second
 * factor, and the refused request is sent again, so the action goes on. Requests refused at the same moment share the
 * one dialog. Cancelling lets the refusal through, and the page says the action did not happen. A person with no
 * password, who signs in through a company login, confirms by signing in through it again (ADR-0048): the page they are
 * on comes back with a new sign-in, recent enough for the action, which they then take again.
 */
export function ReauthenticationDialog() {
  const [open, setOpen] = useState(false);
  const pending = useRef<{ promise: Promise<boolean>; resolve: (confirmed: boolean) => void } | undefined>(undefined);

  useEffect(
    () =>
      onReauthenticationRequired(() => {
        if (!pending.current) {
          let resolve: (confirmed: boolean) => void = () => undefined;
          const promise = new Promise<boolean>((done) => {
            resolve = done;
          });
          pending.current = { promise, resolve };
          setOpen(true);
        }
        return pending.current.promise;
      }),
    [],
  );

  const settle = (confirmed: boolean) => {
    pending.current?.resolve(confirmed);
    pending.current = undefined;
    setOpen(false);
  };

  return open ? <ReauthenticationForm onConfirmed={() => settle(true)} onCancel={() => settle(false)} /> : null;
}

export function ReauthenticationForm({ onConfirmed, onCancel }: { onConfirmed: () => void; onCancel: () => void }) {
  const { t } = useTranslation();
  const [asks, setAsks] = useState<Reauthentication | undefined>();
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [mode, setMode] = useState<"code" | "recovery">("code");
  const [busy, setBusy] = useState<"form" | "passkey" | undefined>();
  const [problem, setProblem] = useState<ShownProblem | undefined>();

  useEffect(() => {
    let live = true;
    reauthenticationOptions().then(
      (answer) => {
        if (live) {
          setAsks(answer);
          setMode(answer.methods.includes("authenticator_app") ? "code" : "recovery");
        }
      },
      (error: unknown) => live && setProblem(mfaProblem(error, t)),
    );
    return () => {
      live = false;
    };
  }, [t]);

  const confirm = async (proof: () => Promise<SecondFactorProof | undefined>, kind: "form" | "passkey") => {
    setBusy(kind);
    setProblem(undefined);
    try {
      await reauthenticate(password, await proof());
      onConfirmed();
    } catch (error) {
      setProblem(mfaProblem(error, t));
    } finally {
      setBusy(undefined);
    }
  };

  const hasApp = asks?.methods.includes("authenticator_app") ?? false;
  const hasRecovery = asks?.methods.includes("recovery_code") ?? false;
  const hasPasskey =
    (asks?.methods.includes("passkey") ?? false) && asks?.passkey_options != null && passkeysSupported();
  const typedFactor = asks?.second_factor === true && (hasApp || hasRecovery);

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    const typed = code.trim();
    void confirm(
      () => Promise.resolve(typedFactor ? (mode === "code" ? { code: typed } : { recovery_code: typed }) : undefined),
      "form",
    );
  };

  const withPasskey = () => {
    const options = asks?.passkey_options;
    if (options) {
      void confirm(async () => ({ passkey: await signWithPasskey(options) }), "passkey");
    }
  };

  const signInAgain = async (providerId: string) => {
    setBusy("form");
    setProblem(undefined);
    try {
      const started = await startSso(providerId, window.location.pathname + window.location.search);
      leaveFor(started.authorization_url);
    } catch (error) {
      setProblem(mfaProblem(error, t));
      setBusy(undefined);
    }
  };

  if (asks?.password === false) {
    const provider = asks.provider;
    return (
      <FormDialog opened title={t("mfa.reauth.title")} onClose={onCancel}>
        <Stack data-reauthentication data-reauthentication-sso>
          <Text size="sm">
            {provider ? t("mfa.reauth.sso.intro", { name: provider.name }) : t("mfa.reauth.sso.noProvider")}
          </Text>
          {problem && <ProblemAlert problem={problem} />}
          <Group justify="flex-end">
            <Button variant="default" onClick={onCancel} disabled={busy !== undefined}>
              {t("dialog.cancel")}
            </Button>
            {provider && (
              <Button loading={busy === "form"} onClick={() => void signInAgain(provider.id)} data-autofocus>
                {t("mfa.reauth.sso.again", { name: provider.name })}
              </Button>
            )}
          </Group>
        </Stack>
      </FormDialog>
    );
  }

  return (
    <FormDialog opened title={t("mfa.reauth.title")} onClose={onCancel}>
      <form onSubmit={submit} noValidate data-reauthentication>
        <Stack>
          <Text size="sm">{t("mfa.reauth.intro")}</Text>
          <PasswordInput
            label={t("mfa.reauth.password")}
            autoComplete="current-password"
            required
            data-autofocus
            value={password}
            onChange={(event) => setPassword(event.currentTarget.value)}
          />
          {typedFactor &&
            (mode === "code" ? (
              <CodeInput label={t("mfa.reauth.code")} value={code} onChange={setCode} />
            ) : (
              <TextInput
                label={t("mfa.reauth.recovery")}
                value={code}
                onChange={(event) => setCode(event.currentTarget.value)}
                autoComplete="off"
                spellCheck={false}
                required
                styles={{ input: { fontFamily: "var(--iron-font-mono)" } }}
              />
            ))}
          {typedFactor && hasApp && hasRecovery && (
            <Button
              variant="subtle"
              size="compact-sm"
              onClick={() => {
                setMode(mode === "code" ? "recovery" : "code");
                setCode("");
              }}
            >
              {mode === "code" ? t("mfa.second.useRecovery") : t("mfa.second.useCode")}
            </Button>
          )}
          {problem && <ProblemAlert problem={problem} />}
          <Group justify="flex-end">
            <Button variant="default" onClick={onCancel} disabled={busy !== undefined}>
              {t("dialog.cancel")}
            </Button>
            {(!asks?.second_factor || typedFactor) && (
              <Button
                type="submit"
                loading={busy === "form"}
                disabled={!asks || busy !== undefined || password.length === 0}
              >
                {t("mfa.reauth.submit")}
              </Button>
            )}
          </Group>
          {hasPasskey && (
            <>
              {typedFactor && <Divider label={t("mfa.second.or")} labelPosition="center" />}
              <Button
                variant={typedFactor ? "default" : "filled"}
                onClick={withPasskey}
                loading={busy === "passkey"}
                disabled={busy !== undefined || password.length === 0}
                leftSection={<IconFingerprint size={18} aria-hidden="true" />}
              >
                {t("mfa.reauth.passkey")}
              </Button>
            </>
          )}
        </Stack>
      </form>
    </FormDialog>
  );
}
