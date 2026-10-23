import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { Alert, Button, CopyButton, Group, Menu, Select, Stack, Text, Textarea, TextInput } from "@mantine/core";
import {
  IconAlertTriangle,
  IconCheck,
  IconChevronDown,
  IconCopy,
  IconKey,
  IconLockOpen,
  IconShieldOff,
  IconShieldX,
  IconUserCheck,
  IconUserCircle,
  IconUserOff,
} from "@tabler/icons-react";
import { z } from "zod";
import { ApiError } from "@/api/client";
import {
  deactivateUser,
  issueRecoveryLink,
  reactivateUser,
  resetFactors,
  revokePrincipal,
  setEmergency,
  startImpersonation,
  unlockUser,
  type UserRecord,
} from "@/api/identity";
import { leaveFor } from "@/auth/leave";
import { useAccessAt } from "@/auth/access";
import { useSession } from "@/auth/session-context";
import { Form, useZodForm } from "@/components/form";
import { ConfirmDialog, FormDialog, ReasonDialog } from "@/components/panels";
import { formatDateTime } from "@/i18n/dates";

const manage = "identity.users.manage";
const impersonate = "identity.impersonation.start";

type Action =
  "unlock" | "recovery" | "resetFactors" | "deactivate" | "reactivate" | "impersonate" | "revoke" | "emergency";

/** The actions that take a reason, or a choice, in a dialog of their own. */
const ownDialogs: readonly Action[] = ["resetFactors", "impersonate", "revoke"];

/** The actions an administrator takes most often, in that order: what the person needs to get back in. */
const frequent: readonly Action[] = ["unlock", "reactivate", "recovery"];
const frequentShown = 2;

/** A link shown once: a recovery link, or the invitation a reactivated person who never started gets. */
interface ShownLink {
  kind: "recovery" | "invitation";
  path: string;
  expiresAt: string;
}

/**
 * What an administrator may do to a person (S3 T2, T3, T5, ADR-0046, ADR-0047, ADR-0049): unlock a locked login, issue a
 * recovery link (shown once, with a copy button, as an invitation is), reset their second factors with a reason,
 * deactivate and reactivate, sign in as them with identity.impersonation.start, revoke everything of theirs, and mark one
 * of the tenant's two emergency accounts. Each shows only where /me/access allows `identity.users.manage` at the person's home unit, and only when it
 * applies to the person as they are; "Reset factors" also only when the record says the reader holds every permission
 * the person holds (the escalation rule). The API checks every one again and says why when it refuses, such as for the
 * last tenant administrator. The high-risk ones ask for a recent sign-in through the shared dialog (TF-248). At most two
 * everyday actions stay in sight (unlock, reactivate, a recovery link); the rest are under More, with deactivation and
 * revoking everything last, apart and in the danger colour, each still behind its confirmation (T5's review).
 */
export function UserActions({ user }: { user: UserRecord }) {
  const { t } = useTranslation();
  const [now] = useState(() => Date.now());
  const session = useSession();
  const queryClient = useQueryClient();
  const access = useAccessAt(user.home_unit ? { unit: user.home_unit.id } : undefined, [manage, impersonate]);
  const [asking, setAsking] = useState<Action | undefined>();
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<{ message: string; correlationId: string | undefined } | undefined>();
  const [shown, setShown] = useState<ShownLink | undefined>();
  const [revoked, setRevoked] = useState<number | undefined>();

  if (access.pending || !(access.can(manage) || access.can(impersonate))) {
    return null;
  }

  // Never while signed in as someone else: those are the person's own administrators' acts, not an impersonator's (ADR-0049).
  const impersonating = session.me?.impersonation != null;
  const managing = access.can(manage);

  const locked = user.locked_until != null && Date.parse(user.locked_until) > now;
  const isPerson = user.type === "user";
  const self = session.me?.id === user.id;
  const breakGlass = user.break_glass_until != null;
  const actions: { action: Action; label: string; icon: ReactNode; danger?: boolean }[] = [
    ...(!impersonating &&
    access.can(impersonate) &&
    isPerson &&
    !self &&
    !breakGlass &&
    user.status === "active" &&
    user.takeover_allowed === true
      ? [
          {
            action: "impersonate" as const,
            label: t("users.actions.impersonate.open"),
            icon: <IconUserCircle size={16} aria-hidden="true" />,
          },
        ]
      : []),
    ...(!managing
      ? []
      : locked
        ? [
            {
              action: "unlock" as const,
              label: t("users.actions.unlock.open"),
              icon: <IconLockOpen size={16} aria-hidden="true" />,
            },
          ]
        : []),
    ...(managing && !impersonating && isPerson && user.status === "active"
      ? [
          {
            action: "recovery" as const,
            label: t("users.actions.recovery.open"),
            icon: <IconKey size={16} aria-hidden="true" />,
          },
        ]
      : []),
    ...(managing && !impersonating && isPerson && !self && (user.factors ?? 0) > 0 && user.takeover_allowed === true
      ? [
          {
            action: "resetFactors" as const,
            label: t("users.actions.resetFactors.open"),
            icon: <IconShieldX size={16} aria-hidden="true" />,
          },
        ]
      : []),
    ...(managing && !impersonating && user.status !== "disabled" && !self
      ? [
          {
            action: "deactivate" as const,
            label: t("users.actions.deactivate.open"),
            icon: <IconUserOff size={16} aria-hidden="true" />,
            danger: true,
          },
        ]
      : []),
    ...(managing && user.status === "disabled"
      ? [
          {
            action: "reactivate" as const,
            label: t("users.actions.reactivate.open"),
            icon: <IconUserCheck size={16} aria-hidden="true" />,
          },
        ]
      : []),
    ...(managing &&
    !impersonating &&
    isPerson &&
    !breakGlass &&
    user.status === "active" &&
    (user.emergency === true || user.takeover_allowed === true)
      ? [
          {
            action: "emergency" as const,
            label: user.emergency ? t("users.actions.emergency.clearOpen") : t("users.actions.emergency.open"),
            icon: <IconAlertTriangle size={16} aria-hidden="true" />,
          },
        ]
      : []),
    ...(managing && !impersonating && !self && user.status !== "disabled"
      ? [
          {
            action: "revoke" as const,
            label: t("users.actions.revoke.open"),
            icon: <IconShieldOff size={16} aria-hidden="true" />,
            danger: true,
          },
        ]
      : []),
  ];
  if (actions.length === 0) {
    return null;
  }

  const run = async (action: Action) => {
    setBusy(true);
    setProblem(undefined);
    try {
      if (action === "unlock") {
        await unlockUser(user.id);
      } else if (action === "recovery") {
        const link = await issueRecoveryLink(user.id);
        setShown({ kind: "recovery", path: link.recovery_link, expiresAt: link.expires_at });
      } else if (action === "deactivate") {
        await deactivateUser(user.id);
      } else if (action === "emergency") {
        await setEmergency(user.id, user.emergency !== true);
      } else {
        const back = await reactivateUser(user.id);
        if (back.invitation_link && back.invitation_expires_at) {
          setShown({ kind: "invitation", path: back.invitation_link, expiresAt: back.invitation_expires_at });
        }
      }
      void queryClient.invalidateQueries({ queryKey: ["identity", "users"] });
    } catch (error) {
      setProblem({
        message: error instanceof ApiError ? error.message : t("errors.unexpected"),
        correlationId: error instanceof ApiError ? error.correlationId : undefined,
      });
    } finally {
      setBusy(false);
      setAsking(undefined);
    }
  };

  // At most two of the everyday actions stay in sight; the rest wait under More, the dangerous ones last and apart.
  const safe = actions.filter((entry) => entry.danger !== true);
  const dangerous = actions.filter((entry) => entry.danger === true);
  const everyday = safe.filter((entry) => frequent.includes(entry.action)).slice(0, frequentShown);
  const inSight = everyday.length > 0 ? everyday : safe.slice(0, 1);
  const moreSafe = safe.filter((entry) => !inSight.includes(entry));

  const name = user.display_name;
  return (
    <Stack gap="xs" align="flex-end" data-user-actions>
      <Group gap="xs" justify="flex-end" wrap="nowrap">
        {inSight.map(({ action, label, icon }) => (
          <Button
            key={action}
            variant="default"
            leftSection={icon}
            onClick={() => setAsking(action)}
            data-action={action}
          >
            {label}
          </Button>
        ))}
        {moreSafe.length + dangerous.length > 0 && (
          <Menu position="bottom-end" withinPortal withInitialFocusPlaceholder={false}>
            <Menu.Target>
              <Button
                variant="default"
                rightSection={<IconChevronDown size={16} aria-hidden="true" />}
                aria-label={t("users.actions.moreLabel", { name })}
                data-action="more"
              >
                {t("users.actions.more")}
              </Button>
            </Menu.Target>
            <Menu.Dropdown miw={240}>
              {moreSafe.map(({ action, label, icon }) => (
                <Menu.Item key={action} leftSection={icon} onClick={() => setAsking(action)} data-action={action}>
                  {label}
                </Menu.Item>
              ))}
              {moreSafe.length > 0 && dangerous.length > 0 && <Menu.Divider role="separator" />}
              {dangerous.map(({ action, label, icon }) => (
                <Menu.Item
                  key={action}
                  c="var(--iron-accent-danger)"
                  leftSection={icon}
                  onClick={() => setAsking(action)}
                  data-action={action}
                >
                  {label}
                </Menu.Item>
              ))}
            </Menu.Dropdown>
          </Menu>
        )}
      </Group>
      {problem && (
        <Alert color="danger" role="alert" maw={480}>
          {problem.message}
          {problem.correlationId && (
            <Text size="xs" c="dimmed" mt={4}>
              {t("errors.correlation", { id: problem.correlationId })}
            </Text>
          )}
        </Alert>
      )}
      {asking === "resetFactors" && (
        <ResetFactorsDialog
          user={user}
          onClose={() => setAsking(undefined)}
          onReset={() => {
            setAsking(undefined);
            setProblem(undefined);
            void queryClient.invalidateQueries({ queryKey: ["identity", "users"] });
          }}
        />
      )}
      {asking === "impersonate" && <ImpersonateDialog user={user} onClose={() => setAsking(undefined)} />}
      {asking === "revoke" && (
        <ReasonDialog
          opened
          title={t("users.actions.revoke.title", { name })}
          message={t(isPerson ? "users.actions.revoke.message" : "users.actions.revoke.messageClient", { name })}
          reasonLabel={t("reason.label")}
          reasonHint={t("users.actions.revoke.reasonHint", { name })}
          confirmLabel={t("users.actions.revoke.confirm")}
          onSubmit={async (reason) => {
            const result = await revokePrincipal(user.id, reason);
            setAsking(undefined);
            setProblem(undefined);
            setRevoked(result.sessions_ended);
            void queryClient.invalidateQueries({ queryKey: ["identity", "users"] });
          }}
          onClose={() => setAsking(undefined)}
        />
      )}
      {revoked !== undefined && (
        <Alert
          color="success"
          role="status"
          maw={480}
          withCloseButton
          closeButtonLabel={t("dialog.close")}
          onClose={() => setRevoked(undefined)}
        >
          {t("users.actions.revoke.done", { name, count: revoked })}
        </Alert>
      )}
      {asking !== undefined && !ownDialogs.includes(asking) && (
        <ConfirmDialog
          opened
          title={t(
            asking === "emergency" && user.emergency
              ? "users.actions.emergency.clearTitle"
              : `users.actions.${asking}.title`,
            { name },
          )}
          message={t(
            asking === "emergency" && user.emergency
              ? "users.actions.emergency.clearMessage"
              : `users.actions.${asking}.message`,
            { name },
          )}
          confirmLabel={t(
            asking === "emergency" && user.emergency
              ? "users.actions.emergency.clearConfirm"
              : `users.actions.${asking}.confirm`,
          )}
          danger={asking === "deactivate"}
          busy={busy}
          onConfirm={() => void run(asking)}
          onCancel={() => setAsking(undefined)}
        />
      )}
      {shown && <LinkDialog link={shown} name={name} onClose={() => setShown(undefined)} />}
    </Stack>
  );
}

interface ResetValues extends Record<string, unknown> {
  reason: string;
}

/**
 * Signing in as a person (TF-025, ADR-0049): why, which the person and the auditors read, and for how long, up to the
 * tenant's maximum. The whole shell then starts again as the person, under a band that says so; the API refuses a person
 * whose permissions the impersonator does not hold, and never lets them confirm who they are, sign or change credentials.
 */
function ImpersonateDialog({ user, onClose }: { user: UserRecord; onClose: () => void }) {
  const { t } = useTranslation();
  const [minutes, setMinutes] = useState<string | null>(null);
  const name = user.display_name;
  return (
    <ReasonDialog
      opened
      title={t("users.actions.impersonate.title", { name })}
      message={t("users.actions.impersonate.message", { name })}
      reasonLabel={t("reason.label")}
      reasonHint={t("users.actions.impersonate.reasonHint", { name })}
      confirmLabel={t("users.actions.impersonate.confirm")}
      extra={
        <Select
          label={t("users.actions.impersonate.minutes")}
          description={t("users.actions.impersonate.minutesHint")}
          placeholder={t("users.actions.impersonate.longest")}
          data={["15", "30", "60", "120"].map((value) => ({
            value,
            label: t("users.actions.impersonate.minutesValue", { count: Number(value) }),
          }))}
          value={minutes}
          onChange={setMinutes}
          clearable
        />
      }
      onSubmit={async (reason) => {
        await startImpersonation(user.id, reason, minutes ? Number(minutes) : undefined);
        // The whole shell starts again as the person, so nothing of the impersonator's pages stays in memory.
        leaveFor("/");
      }}
      onClose={onClose}
    />
  );
}

/**
 * Resetting a person's second factors (TF-013 AC3, ADR-0047): what it does, and the reason, which their security activity
 * and the audit trail keep. Their sessions end, and they enrol again at their next sign-in.
 */
function ResetFactorsDialog({
  user,
  onClose,
  onReset,
}: {
  user: UserRecord;
  onClose: () => void;
  onReset: () => void;
}) {
  const { t } = useTranslation();
  const name = user.display_name;
  const schema = useMemo(
    () => z.object({ reason: z.string().trim().min(1, t("users.actions.resetFactors.reasonRequired")).max(500) }),
    [t],
  );
  const form = useZodForm<ResetValues>({ schema, initialValues: { reason: "" } });
  const submit = async (values: ResetValues) => {
    await resetFactors(user.id, values.reason.trim());
    onReset();
  };
  return (
    <FormDialog opened title={t("users.actions.resetFactors.title", { name })} onClose={onClose}>
      <Form
        form={form}
        onSubmit={submit}
        submitLabel={t("users.actions.resetFactors.confirm")}
        secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      >
        <Text size="sm">{t("users.actions.resetFactors.message", { name })}</Text>
        <Textarea
          label={t("users.actions.resetFactors.reason")}
          description={t("users.actions.resetFactors.reasonHint", { name })}
          required
          autosize
          minRows={2}
          maxLength={500}
          data-autofocus
          {...form.getInputProps("reason")}
        />
      </Form>
    </FormDialog>
  );
}

/** The link, once: to copy and hand over. It lives in this dialog's memory only, so it is gone when the dialog closes. */
function LinkDialog({ link, name, onClose }: { link: ShownLink; name: string; onClose: () => void }) {
  const { t } = useTranslation();
  const field = useRef<HTMLInputElement>(null);
  const url = new URL(link.path, window.location.origin).toString();
  const expires = formatDateTime(link.expiresAt);
  useEffect(() => {
    field.current?.focus();
    field.current?.select();
  }, []);
  return (
    <FormDialog opened title={t(`users.actions.link.${link.kind}.title`, { name })} onClose={onClose}>
      <Stack data-shown-link={link.kind}>
        <Text size="sm">{t(`users.actions.link.${link.kind}.text`, { name })}</Text>
        <TextInput
          ref={field}
          data-autofocus
          label={t(`users.actions.link.${link.kind}.label`)}
          value={url}
          readOnly
          onFocus={(event) => event.currentTarget.select()}
        />
        <CopyButton value={url}>
          {({ copied, copy }) => (
            <Group gap="sm">
              <Button
                variant="default"
                onClick={copy}
                leftSection={
                  copied ? <IconCheck size={16} aria-hidden="true" /> : <IconCopy size={16} aria-hidden="true" />
                }
              >
                {copied ? t("users.invite.copied") : t("users.invite.copy")}
              </Button>
            </Group>
          )}
        </CopyButton>
        <Alert color="info">{t(`users.actions.link.${link.kind}.byHand`, { when: expires })}</Alert>
        <Group justify="flex-end">
          <Button onClick={onClose}>{t("users.actions.link.done")}</Button>
        </Group>
      </Stack>
    </FormDialog>
  );
}
