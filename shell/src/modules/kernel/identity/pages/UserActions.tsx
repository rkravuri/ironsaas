import { useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { Alert, Button, CopyButton, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconCheck, IconCopy, IconKey, IconLockOpen, IconUserCheck, IconUserOff } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { deactivateUser, issueRecoveryLink, reactivateUser, unlockUser, type UserRecord } from "@/api/identity";
import { useAccessAt } from "@/auth/access";
import { useSession } from "@/auth/session-context";
import { ConfirmDialog, FormDialog } from "@/components/panels";

const manage = "identity.users.manage";

type Action = "unlock" | "recovery" | "deactivate" | "reactivate";

/** A link shown once: a recovery link, or the invitation a reactivated person who never started gets. */
interface ShownLink {
  kind: "recovery" | "invitation";
  path: string;
  expiresAt: string;
}

/**
 * What an administrator may do to a person (S3 T2, ADR-0046): unlock a locked login, issue a recovery link (shown once,
 * with a copy button, as an invitation is), deactivate and reactivate. Each shows only where /me/access allows
 * `identity.users.manage` at the person's home unit, and only when it applies to the person as they are; the API checks
 * every one again and says why when it refuses, such as for the last tenant administrator.
 */
export function UserActions({ user }: { user: UserRecord }) {
  const { t } = useTranslation();
  const [now] = useState(() => Date.now());
  const session = useSession();
  const queryClient = useQueryClient();
  const access = useAccessAt(user.home_unit ? { unit: user.home_unit.id } : undefined, [manage]);
  const [asking, setAsking] = useState<Action | undefined>();
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<{ message: string; correlationId: string | undefined } | undefined>();
  const [shown, setShown] = useState<ShownLink | undefined>();

  if (access.pending || !access.can(manage)) {
    return null;
  }

  const locked = user.locked_until != null && Date.parse(user.locked_until) > now;
  const isPerson = user.type === "user";
  const self = session.me?.id === user.id;
  const actions: { action: Action; label: string; icon: ReactNode; danger?: boolean }[] = [
    ...(locked
      ? [
          {
            action: "unlock" as const,
            label: t("users.actions.unlock.open"),
            icon: <IconLockOpen size={16} aria-hidden="true" />,
          },
        ]
      : []),
    ...(isPerson && user.status === "active"
      ? [
          {
            action: "recovery" as const,
            label: t("users.actions.recovery.open"),
            icon: <IconKey size={16} aria-hidden="true" />,
          },
        ]
      : []),
    ...(user.status !== "disabled" && !self
      ? [
          {
            action: "deactivate" as const,
            label: t("users.actions.deactivate.open"),
            icon: <IconUserOff size={16} aria-hidden="true" />,
            danger: true,
          },
        ]
      : []),
    ...(user.status === "disabled"
      ? [
          {
            action: "reactivate" as const,
            label: t("users.actions.reactivate.open"),
            icon: <IconUserCheck size={16} aria-hidden="true" />,
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

  const name = user.display_name;
  return (
    <Stack gap="xs" align="flex-end" data-user-actions>
      <Group gap="xs" justify="flex-end">
        {actions.map(({ action, label, icon, danger }) => (
          <Button
            key={action}
            variant="default"
            leftSection={icon}
            {...(danger ? { color: "danger" } : {})}
            onClick={() => setAsking(action)}
            data-action={action}
          >
            {label}
          </Button>
        ))}
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
      {asking !== undefined && (
        <ConfirmDialog
          opened
          title={t(`users.actions.${asking}.title`, { name })}
          message={t(`users.actions.${asking}.message`, { name })}
          confirmLabel={t(`users.actions.${asking}.confirm`)}
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

/** The link, once: to copy and hand over. It lives in this dialog's memory only, so it is gone when the dialog closes. */
function LinkDialog({ link, name, onClose }: { link: ShownLink; name: string; onClose: () => void }) {
  const { t } = useTranslation();
  const field = useRef<HTMLInputElement>(null);
  const url = new URL(link.path, window.location.origin).toString();
  const expires = new Date(link.expiresAt).toLocaleString();
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
