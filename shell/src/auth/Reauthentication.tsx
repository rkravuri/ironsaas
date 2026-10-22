import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Divider, Group, PasswordInput, Stack, Text, TextInput } from "@mantine/core";
import { IconFingerprint } from "@tabler/icons-react";
import { onReauthenticationRequired } from "@/api/client";
import {
  reauthenticate,
  reauthenticateThroughProvider,
  reauthenticationOptions,
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
 * password, who signs in through a company login, confirms with a fresh sign-in there (TF-248 AC6, ADR-0048), after their
 * second factor here when they have one: the page they are on comes back with the session confirmed, and the action
 * ready to take again, or with why it was not.
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

  /** The second factor here first, when the person has one, then the fresh sign-in at the provider, which leaves the page. */
  const signInAgain = async (proof: () => Promise<SecondFactorProof | undefined>, kind: "form" | "passkey") => {
    setBusy(kind);
    setProblem(undefined);
    try {
      const started = await reauthenticateThroughProvider(
        window.location.pathname + window.location.search,
        await proof(),
      );
      leaveFor(started.authorization_url);
    } catch (error) {
      setProblem(mfaProblem(error, t));
      setBusy(undefined);
    }
  };

  if (asks?.password === false) {
    const provider = asks.provider;
    const submitThroughProvider = (event: React.FormEvent) => {
      event.preventDefault();
      const typed = code.trim();
      void signInAgain(
        () => Promise.resolve(typedFactor ? (mode === "code" ? { code: typed } : { recovery_code: typed }) : undefined),
        "form",
      );
    };
    return (
      <FormDialog opened title={t("mfa.reauth.title")} onClose={onCancel}>
        <form onSubmit={submitThroughProvider} noValidate data-reauthentication data-reauthentication-sso>
          <Stack>
            <Text size="sm">
              {provider
                ? t(asks.second_factor ? "mfa.reauth.sso.introFactor" : "mfa.reauth.sso.intro", { name: provider.name })
                : t("mfa.reauth.sso.noProvider")}
            </Text>
            {provider &&
              typedFactor &&
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
            {provider && typedFactor && hasApp && hasRecovery && (
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
              {provider && (!asks.second_factor || typedFactor) && (
                <Button type="submit" loading={busy === "form"} disabled={busy !== undefined} data-autofocus>
                  {t("mfa.reauth.sso.again", { name: provider.name })}
                </Button>
              )}
            </Group>
            {provider && hasPasskey && (
              <>
                {typedFactor && <Divider label={t("mfa.second.or")} labelPosition="center" />}
                <Button
                  variant={typedFactor ? "default" : "filled"}
                  onClick={() => {
                    const options = asks.passkey_options;
                    if (options) {
                      void signInAgain(async () => ({ passkey: await signWithPasskey(options) }), "passkey");
                    }
                  }}
                  loading={busy === "passkey"}
                  disabled={busy !== undefined}
                  leftSection={<IconFingerprint size={18} aria-hidden="true" />}
                >
                  {t("mfa.reauth.sso.passkeyAgain", { name: provider.name })}
                </Button>
              </>
            )}
          </Stack>
        </form>
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
