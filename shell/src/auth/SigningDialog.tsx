import { useEffect, useState, type ReactNode } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import {
  Button,
  Divider,
  Group,
  PasswordInput,
  SegmentedControl,
  Stack,
  Text,
  TextInput,
  Textarea,
} from "@mantine/core";
import { IconFingerprint, IconSignature } from "@tabler/icons-react";
import {
  credentialsWith,
  fetchSigningChallenge,
  type SigningChallenge,
  type SigningCredentials,
} from "@/api/compliance";
import { reauthenticateThroughProvider, type SecondFactorProof } from "@/api/identity";
import { useSession } from "./session-context";
import { FormDialog } from "@/components/panels";
import { leaveFor } from "./leave";
import { CodeInput, ProblemAlert } from "./mfa";
import type { ShownProblem } from "./mfa-problems";
import { signingProblem } from "./signing-problems";
import { passkeysSupported, signWithPasskey } from "./webauthn";

const commentLimit = 500;

export interface SigningDialogProps {
  opened: boolean;
  /** The document type signed: its policy and the signer's rule come from the API. */
  recordType: string;
  title: string;
  /** What is signed, in a sentence or two. */
  summary: ReactNode;
  /** The meanings this signature may have; one is shown, more are a choice. */
  meanings: readonly string[];
  /** Whether the signer may add a comment (the record keeps it with the signature). */
  comment?: boolean;
  onSign: (credentials: SigningCredentials, meaning: string, comment: string | undefined) => Promise<void>;
  onClose: () => void;
}

/**
 * The signing dialog (TF-063, ADR-0051): the meaning, a comment, and the credentials the components rule asks for now.
 * The first signature of a session asks for the login identifier and the password, and the second factor when the
 * person has one; a later one for the password, and the second factor when the tenant asks; a person who signs in
 * through their company login signs after a fresh sign-in there, which leaves the page and brings it back confirmed.
 * Someone who may never sign (signed in as someone else, acting for someone) is told why, and nothing is asked.
 */
export function SigningDialog({ opened, ...props }: SigningDialogProps) {
  const { t } = useTranslation();
  // The form mounts on every opening: it asks the API afresh, and starts empty.
  return (
    <FormDialog opened={opened} title={props.title} onClose={props.onClose}>
      {opened && <SigningForm {...props} t={t} />}
    </FormDialog>
  );
}

function SigningForm({
  recordType,
  summary,
  meanings,
  comment: commentAllowed = true,
  onSign,
  onClose,
  t,
}: Omit<SigningDialogProps, "opened" | "title"> & { t: TFunction }) {
  const session = useSession();
  const [challenge, setChallenge] = useState<SigningChallenge | undefined>();
  const [refusal, setRefusal] = useState<ShownProblem | undefined>();
  const [meaning, setMeaning] = useState(meanings[0] ?? "signed");
  const [comment, setComment] = useState("");
  const [loginId, setLoginId] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [mode, setMode] = useState<"code" | "recovery">("code");
  const [busy, setBusy] = useState<"form" | "passkey" | undefined>();
  const [problem, setProblem] = useState<ShownProblem | undefined>();

  useEffect(() => {
    let live = true;
    fetchSigningChallenge(recordType).then(
      (answer) => {
        if (live) {
          setChallenge(answer);
          setMode(answer.methods.includes("authenticator_app") ? "code" : "recovery");
        }
      },
      (error: unknown) => live && setRefusal(signingProblem(error, t)),
    );
    return () => {
      live = false;
    };
  }, [recordType, t]);

  const allowed = meanings.filter((m) => challenge === undefined || challenge.policy.meanings.includes(m));
  const hasApp = challenge?.methods.includes("authenticator_app") ?? false;
  const hasRecovery = challenge?.methods.includes("recovery_code") ?? false;
  const typedFactor = challenge?.second_factor === true && (hasApp || hasRecovery);
  const hasPasskey =
    challenge?.second_factor === true &&
    challenge.methods.includes("passkey") &&
    challenge.passkey_options != null &&
    passkeysSupported();
  const throughProvider = challenge !== undefined && challenge.reauthenticate && !challenge.password;
  const typedProof = (): SecondFactorProof | undefined => {
    const typed = code.trim();
    return typedFactor && typed !== "" ? (mode === "code" ? { code: typed } : { recovery_code: typed }) : undefined;
  };
  const trimmedComment = comment.trim() === "" ? undefined : comment.trim();

  const sign = async (proof: () => Promise<SecondFactorProof | undefined>, kind: "form" | "passkey") => {
    setBusy(kind);
    setProblem(undefined);
    try {
      const base =
        challenge?.reauthenticate === false || throughProvider
          ? {}
          : { password, ...(challenge?.login_id ? { login_id: loginId.trim() } : {}) };
      await onSign(credentialsWith(base, await proof()), meaning, trimmedComment);
    } catch (error) {
      setProblem(signingProblem(error, t));
      setBusy(undefined);
    }
  };

  /** The fresh sign-in at the company login: the second factor here first when the person has one, then the provider. */
  const signInAgain = async () => {
    setBusy("form");
    setProblem(undefined);
    try {
      const started = await reauthenticateThroughProvider(
        window.location.pathname + window.location.search,
        typedProof(),
      );
      leaveFor(started.authorization_url);
    } catch (error) {
      setProblem(signingProblem(error, t));
      setBusy(undefined);
    }
  };

  if (refusal) {
    return (
      <Stack data-signing-dialog>
        <ProblemAlert problem={refusal} />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t("dialog.close")}
          </Button>
        </Group>
      </Stack>
    );
  }

  const factorField =
    typedFactor &&
    (mode === "code" ? (
      <CodeInput label={t("signing.code")} value={code} onChange={setCode} />
    ) : (
      <TextInput
        label={t("signing.recovery")}
        value={code}
        onChange={(event) => setCode(event.currentTarget.value)}
        autoComplete="off"
        spellCheck={false}
        required
        styles={{ input: { fontFamily: "var(--iron-font-mono)" } }}
      />
    ));
  const switchFactor = typedFactor && hasApp && hasRecovery && (
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
  );

  return (
    <form
      noValidate
      data-signing-dialog
      onSubmit={(event) => {
        event.preventDefault();
        if (throughProvider && !challenge.provider_confirmed) {
          void signInAgain();
        } else {
          void sign(() => Promise.resolve(throughProvider ? undefined : typedProof()), "form");
        }
      }}
    >
      <Stack>
        <Text size="sm">{summary}</Text>
        {allowed.length > 1 ? (
          <Stack gap={4}>
            <Text size="sm" fw={600} id="signing-meaning">
              {t("signing.meaning")}
            </Text>
            <SegmentedControl
              aria-labelledby="signing-meaning"
              value={meaning}
              onChange={setMeaning}
              data={allowed.map((m) => ({ value: m, label: t(`signatures.meanings.${m}`, { defaultValue: m }) }))}
            />
          </Stack>
        ) : (
          <Text size="sm" data-meaning>
            <Text span fw={600}>
              {t("signing.meaning")}:
            </Text>{" "}
            {t(`signatures.meanings.${meaning}`, { defaultValue: meaning })}
          </Text>
        )}
        {commentAllowed && (
          <Textarea
            label={t("signing.comment")}
            description={t("signing.commentHint")}
            autosize
            minRows={2}
            maxRows={5}
            maxLength={commentLimit}
            value={comment}
            onChange={(event) => setComment(event.currentTarget.value)}
          />
        )}
        <Group gap="xs" wrap="nowrap" align="flex-start" data-statement>
          <IconSignature size={18} aria-hidden="true" style={{ flexShrink: 0 }} />
          <Text size="sm">{t("signing.statement", { name: session.me?.display_name ?? "" })}</Text>
        </Group>
        {challenge === undefined ? (
          <Text size="sm" c="dimmed">
            {t("signing.loading")}
          </Text>
        ) : !challenge.reauthenticate ? (
          <Text size="sm">{t("signing.sessionOnly")}</Text>
        ) : throughProvider ? (
          <>
            <Text size="sm">
              {challenge.provider_confirmed
                ? t("signing.provider.confirmed", { name: challenge.provider_name ?? "" })
                : t(challenge.second_factor ? "signing.provider.introFactor" : "signing.provider.intro", {
                    name: challenge.provider_name ?? "",
                  })}
            </Text>
            {!challenge.provider_confirmed && factorField}
            {!challenge.provider_confirmed && switchFactor}
          </>
        ) : (
          <>
            <Text size="sm">{t(challenge.first_in_session ? "signing.first" : "signing.later")}</Text>
            {challenge.login_id && (
              <TextInput
                label={t("signing.loginId")}
                autoComplete="username"
                required
                data-autofocus
                value={loginId}
                onChange={(event) => setLoginId(event.currentTarget.value)}
              />
            )}
            <PasswordInput
              label={t("signing.password")}
              autoComplete="current-password"
              required
              {...(challenge.login_id ? {} : { "data-autofocus": true })}
              value={password}
              onChange={(event) => setPassword(event.currentTarget.value)}
            />
            {factorField}
            {switchFactor}
          </>
        )}
        {problem && <ProblemAlert problem={problem} />}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={busy !== undefined}>
            {t("dialog.cancel")}
          </Button>
          {challenge !== undefined &&
            (throughProvider && !challenge.provider_confirmed ? (
              <Button type="submit" loading={busy === "form"} disabled={busy !== undefined}>
                {t("signing.provider.again", { name: challenge.provider_name ?? "" })}
              </Button>
            ) : (
              (!challenge.second_factor || typedFactor || throughProvider || !challenge.reauthenticate) && (
                <Button
                  type="submit"
                  leftSection={<IconSignature size={16} aria-hidden="true" />}
                  loading={busy === "form"}
                  disabled={
                    busy !== undefined ||
                    (challenge.reauthenticate && !throughProvider && password.length === 0) ||
                    (challenge.login_id && loginId.trim() === "")
                  }
                >
                  {t("signing.sign")}
                </Button>
              )
            ))}
        </Group>
        {hasPasskey && !throughProvider && (
          <>
            {typedFactor && <Divider label={t("mfa.second.or")} labelPosition="center" />}
            <Button
              variant={typedFactor ? "default" : "filled"}
              onClick={() => {
                const options = challenge?.passkey_options;
                if (options) {
                  void sign(async () => ({ passkey: await signWithPasskey(options) }), "passkey");
                }
              }}
              loading={busy === "passkey"}
              disabled={
                busy !== undefined || password.length === 0 || (challenge?.login_id === true && loginId.trim() === "")
              }
              leftSection={<IconFingerprint size={18} aria-hidden="true" />}
            >
              {t("signing.passkey")}
            </Button>
          </>
        )}
      </Stack>
    </form>
  );
}
