import { useEffect, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import {
  Alert,
  Button,
  Checkbox,
  CopyButton,
  Group,
  List,
  Paper,
  SimpleGrid,
  Stack,
  Text,
  Textarea,
  TextInput,
  UnstyledButton,
} from "@mantine/core";
import { IconCheck, IconCopy, IconDeviceMobile, IconDownload, IconFingerprint, IconPrinter } from "@tabler/icons-react";
import type { AuthenticatorAppSetup } from "@/api/identity";
import { mfaProblem, type ShownProblem } from "./mfa-problems";
import { QrCode } from "./QrCode";
import { createPasskey, passkeysSupported } from "./webauthn";

/** A refusal with its correlation id, as every form of the second factor shows it. */
export function ProblemAlert({ problem, children }: { problem: ShownProblem; children?: ReactNode }) {
  const { t } = useTranslation();
  return (
    <Alert color="danger" role="alert">
      {problem.message}
      {problem.correlationId && (
        <Text size="xs" c="dimmed" mt={4}>
          {t("errors.correlation", { id: problem.correlationId })}
        </Text>
      )}
      {children}
    </Alert>
  );
}

/** A code field for an authenticator app: digits, the phone's own suggestion of a one-time code, six at a time. */
export function CodeInput({
  label,
  value,
  onChange,
  autoFocus = false,
  description,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  autoFocus?: boolean;
  description?: string;
}) {
  return (
    <TextInput
      label={label}
      description={description}
      value={value}
      onChange={(event) => onChange(event.currentTarget.value)}
      inputMode="numeric"
      autoComplete="one-time-code"
      maxLength={7}
      required
      data-autofocus={autoFocus || undefined}
      autoFocus={autoFocus}
    />
  );
}

/**
 * Recovery codes, shown once (TF-013 AC3, ADR-0047): ten codes to copy, download or print, and a box to tick before going
 * on, since they are never shown again. They live in this component's memory only.
 */
export function RecoveryCodesPanel({
  codes,
  account,
  onDone,
  doneLabel,
}: {
  codes: readonly string[];
  account: string;
  onDone: () => void;
  doneLabel?: string;
}) {
  const { t } = useTranslation();
  const [saved, setSaved] = useState(false);
  const text = [t("mfa.codes.fileHeading", { account }), "", ...codes, ""].join("\n");

  const download = () => {
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain;charset=utf-8" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = t("mfa.codes.fileName");
    link.click();
    URL.revokeObjectURL(url);
  };

  // A page of its own in a frame that is never shown, so the printout holds the codes and nothing else of the shell.
  const print = () => {
    const frame = document.createElement("iframe");
    frame.setAttribute("aria-hidden", "true");
    frame.style.position = "fixed";
    frame.style.width = "0";
    frame.style.height = "0";
    frame.style.border = "0";
    document.body.appendChild(frame);
    const page = frame.contentDocument;
    if (page) {
      const pre = page.createElement("pre");
      pre.textContent = text;
      page.body.appendChild(pre);
      frame.contentWindow?.print();
    }
    setTimeout(() => frame.remove(), 1000);
  };

  return (
    <Stack data-recovery-codes>
      <Text size="sm">{t("mfa.codes.intro")}</Text>
      <Paper withBorder p="md">
        <List
          listStyleType="none"
          aria-label={t("mfa.codes.listLabel")}
          ff="var(--iron-font-mono)"
          spacing={4}
          styles={{ root: { columns: 2 } }}
        >
          {codes.map((code) => (
            <List.Item key={code}>{code}</List.Item>
          ))}
        </List>
      </Paper>
      <Group gap="sm">
        <CopyButton value={codes.join("\n")}>
          {({ copied, copy }) => (
            <Button
              variant="default"
              onClick={copy}
              leftSection={
                copied ? <IconCheck size={16} aria-hidden="true" /> : <IconCopy size={16} aria-hidden="true" />
              }
            >
              {copied ? t("mfa.codes.copied") : t("mfa.codes.copy")}
            </Button>
          )}
        </CopyButton>
        <Button variant="default" onClick={download} leftSection={<IconDownload size={16} aria-hidden="true" />}>
          {t("mfa.codes.download")}
        </Button>
        <Button variant="default" onClick={print} leftSection={<IconPrinter size={16} aria-hidden="true" />}>
          {t("mfa.codes.print")}
        </Button>
      </Group>
      <Checkbox
        label={t("mfa.codes.saved")}
        checked={saved}
        onChange={(event) => setSaved(event.currentTarget.checked)}
      />
      <Group justify="flex-end">
        <Button onClick={onDone} disabled={!saved}>
          {doneLabel ?? t("mfa.codes.continue")}
        </Button>
      </Group>
    </Stack>
  );
}

/** The calls an enrolment makes: at sign-in through the sign-in's cookie, in My profile through the session. */
export interface EnrolmentCalls<T> {
  startApp: () => Promise<AuthenticatorAppSetup>;
  confirmApp: (code: string, name: string | undefined) => Promise<T>;
  passkeyOptions: () => Promise<Record<string, unknown>>;
  registerPasskey: (name: string | undefined, credential: unknown) => Promise<T>;
}

type Choice = "authenticator_app" | "passkey";

/**
 * Enrolling a second factor (TF-013, ADR-0047): the kinds the tenant allows, then an authenticator app with its QR code,
 * its key as text and the first code, or a passkey with the name the person chooses. The factor is saved only once it
 * has proved itself. <paramref name="onProblem"/> lets the caller take a refusal it handles itself, such as a sign-in
 * that ended; it answers true when it did.
 */
export function FactorEnrolment<T>({
  allowed,
  calls,
  onEnrolled,
  onProblem,
}: {
  allowed: readonly string[];
  calls: EnrolmentCalls<T>;
  onEnrolled: (result: T) => void;
  onProblem?: (error: unknown) => boolean;
}) {
  const { t } = useTranslation();
  const kinds = (["authenticator_app", "passkey"] as const).filter((kind) => allowed.includes(kind));
  const [choice, setChoice] = useState<Choice | undefined>(kinds.length === 1 ? kinds[0] : undefined);

  if (choice === undefined) {
    return (
      <Stack gap="sm" data-factor-choice>
        <Text size="sm" fw={600} id="factor-choice">
          {t("mfa.enrol.choose")}
        </Text>
        <SimpleGrid cols={{ base: 1, xs: 2 }} role="group" aria-labelledby="factor-choice">
          {kinds.map((kind) => (
            <UnstyledButton key={kind} onClick={() => setChoice(kind)} data-choose={kind}>
              <Paper withBorder p="md" h="100%">
                <Group gap="sm" wrap="nowrap" align="flex-start">
                  {kind === "passkey" ? (
                    <IconFingerprint size={24} aria-hidden="true" />
                  ) : (
                    <IconDeviceMobile size={24} aria-hidden="true" />
                  )}
                  <Stack gap={2}>
                    <Text fw={600}>{t(`mfa.kinds.${kind}`)}</Text>
                    <Text size="sm" c="dimmed">
                      {t(`mfa.enrol.${kind}Text`)}
                    </Text>
                  </Stack>
                </Group>
              </Paper>
            </UnstyledButton>
          ))}
        </SimpleGrid>
      </Stack>
    );
  }

  const back =
    kinds.length > 1 ? (
      <Button variant="subtle" onClick={() => setChoice(undefined)} data-choose-again>
        {t("mfa.enrol.back")}
      </Button>
    ) : null;
  return choice === "authenticator_app" ? (
    <AppSetup calls={calls} onEnrolled={onEnrolled} onProblem={onProblem} back={back} />
  ) : (
    <PasskeySetup calls={calls} onEnrolled={onEnrolled} onProblem={onProblem} back={back} />
  );
}

function AppSetup<T>({
  calls,
  onEnrolled,
  onProblem,
  back,
}: {
  calls: EnrolmentCalls<T>;
  onEnrolled: (result: T) => void;
  onProblem: ((error: unknown) => boolean) | undefined;
  back: ReactNode;
}) {
  const { t } = useTranslation();
  const [setup, setSetup] = useState<AuthenticatorAppSetup | undefined>();
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<ShownProblem | undefined>();

  useEffect(() => {
    let live = true;
    calls.startApp().then(
      (started) => live && setSetup(started),
      (error: unknown) => {
        if (live && !onProblem?.(error)) {
          setProblem(mfaProblem(error, t));
        }
      },
    );
    return () => {
      live = false;
    };
    // Started once: a new secret every render would be a new app.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setProblem(undefined);
    try {
      onEnrolled(await calls.confirmApp(code.trim(), name.trim() || undefined));
    } catch (error) {
      if (!onProblem?.(error)) {
        setProblem(mfaProblem(error, t));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(event) => void submit(event)} noValidate data-app-setup>
      <Stack>
        {setup ? (
          <>
            <Text size="sm">{t("mfa.app.scan")}</Text>
            <Group align="flex-start" gap="lg">
              <QrCode value={setup.uri} label={t("mfa.app.qrLabel")} />
              <Stack gap="xs" style={{ flex: 1, minWidth: 220 }}>
                {/* In groups of four, as apps take a typed key with or without spaces; the whole key, wrapped, never cut off. */}
                <Textarea
                  label={t("mfa.app.secret")}
                  value={setup.secret.match(/.{1,4}/g)?.join(" ") ?? setup.secret}
                  readOnly
                  autosize
                  minRows={1}
                  styles={{ input: { fontFamily: "var(--iron-font-mono)" } }}
                />
                <CopyButton value={setup.secret}>
                  {({ copied, copy }) => (
                    <Button
                      variant="default"
                      size="xs"
                      onClick={copy}
                      leftSection={
                        copied ? <IconCheck size={14} aria-hidden="true" /> : <IconCopy size={14} aria-hidden="true" />
                      }
                    >
                      {copied ? t("mfa.app.copied") : t("mfa.app.copy")}
                    </Button>
                  )}
                </CopyButton>
              </Stack>
            </Group>
            <TextInput
              label={t("mfa.app.name")}
              description={t("mfa.app.nameHint")}
              placeholder={t("mfa.app.defaultName")}
              value={name}
              onChange={(event) => setName(event.currentTarget.value)}
              maxLength={100}
            />
            <CodeInput label={t("mfa.app.code")} value={code} onChange={setCode} />
          </>
        ) : (
          !problem && <Text size="sm">{t("mfa.app.starting")}</Text>
        )}
        {problem && <ProblemAlert problem={problem} />}
        <Group justify="space-between">
          {back ?? <span />}
          <Button type="submit" loading={busy} disabled={!setup || code.trim().length === 0}>
            {t("mfa.app.confirm")}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}

function PasskeySetup<T>({
  calls,
  onEnrolled,
  onProblem,
  back,
}: {
  calls: EnrolmentCalls<T>;
  onEnrolled: (result: T) => void;
  onProblem: ((error: unknown) => boolean) | undefined;
  back: ReactNode;
}) {
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<ShownProblem | undefined>();
  const supported = passkeysSupported();

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setProblem(undefined);
    try {
      const credential = await createPasskey(await calls.passkeyOptions());
      onEnrolled(await calls.registerPasskey(name.trim() || undefined, credential));
    } catch (error) {
      if (!onProblem?.(error)) {
        setProblem(mfaProblem(error, t));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(event) => void submit(event)} noValidate data-passkey-setup>
      <Stack>
        <Text size="sm">{t("mfa.passkey.intro")}</Text>
        <TextInput
          label={t("mfa.passkey.name")}
          description={t("mfa.passkey.nameHint")}
          placeholder={t("mfa.passkey.defaultName")}
          value={name}
          onChange={(event) => setName(event.currentTarget.value)}
          maxLength={100}
        />
        {!supported && (
          <Alert color="warning" role="status">
            {t("mfa.passkey.unsupported")}
          </Alert>
        )}
        {problem && <ProblemAlert problem={problem} />}
        <Group justify="space-between">
          {back ?? <span />}
          <Button
            type="submit"
            loading={busy}
            disabled={!supported}
            leftSection={<IconFingerprint size={16} aria-hidden="true" />}
          >
            {t("mfa.passkey.create")}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}
