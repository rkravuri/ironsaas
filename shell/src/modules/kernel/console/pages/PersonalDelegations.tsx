import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Badge, Button, Group, Stack, Text } from "@mantine/core";
import { IconPlus, IconUserShare } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { endDelegation, fetchMyDelegations, type Delegation } from "@/api/authorization";
import { useSession } from "@/auth/session-context";
import { ConfirmDialog, EmptyState, LoadingState } from "@/components/panels";
import { dutyList } from "@/shell/acting";
import { DelegationDialog } from "./DelegationDialog";
import { isRunning, periodLabel, statusTone } from "./delegation-words";

const mineKey = ["authz", "delegations", "mine"] as const;

/**
 * The person's own delegations (TF-037, ADR-0037), for My profile: those they give and hold, each a sentence with its
 * period, reason and status in words; delegating their own duties; ending one they are part of; and Act for on an
 * active one held. Anyone may do all of it, so it lives with the person, not in the console.
 */
export function PersonalDelegations() {
  const { t, i18n } = useTranslation();
  const session = useSession();
  const queryClient = useQueryClient();
  const mine = useQuery({ queryKey: mineKey, queryFn: ({ signal }) => fetchMyDelegations(signal) });
  const [creating, setCreating] = useState(false);
  const [ending, setEnding] = useState<Delegation | undefined>();
  const [busy, setBusy] = useState(false);
  const [acting, setActing] = useState<string | undefined>();
  const [failure, setFailure] = useState<ApiError | undefined>();
  const me = session.me;
  const actingNow = me?.acting_for != null;

  // /me answers the active delegations both ways, which the header's control follows.
  const changed = () => Promise.all([queryClient.invalidateQueries({ queryKey: mineKey }), session.refresh()]);

  const run = async (action: () => Promise<void>) => {
    setFailure(undefined);
    try {
      await action();
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    }
  };

  const end = () =>
    run(async () => {
      if (!ending) {
        return;
      }
      setBusy(true);
      try {
        await endDelegation(ending.id);
        await changed();
      } finally {
        setBusy(false);
        setEnding(undefined);
      }
    });

  const actFor = (delegator: string) =>
    run(async () => {
      setActing(delegator);
      try {
        await session.actFor(delegator);
      } finally {
        setActing(undefined);
      }
    });

  return (
    <Stack gap="md">
      <Group justify="space-between" align="flex-start">
        <Text size="sm" c="dimmed" maw={560}>
          {t("delegations.yours.intro")}
        </Text>
        <Button
          leftSection={<IconPlus size={16} aria-hidden="true" />}
          onClick={() => setCreating(true)}
          disabled={actingNow}
        >
          {t("delegations.create.open")}
        </Button>
      </Group>
      {actingNow && (
        <Text size="sm" c="dimmed">
          {t("delegations.yours.whileActing")}
        </Text>
      )}
      {failure && (
        <Alert
          color="danger"
          role="alert"
          withCloseButton
          closeButtonLabel={t("dialog.close")}
          onClose={() => setFailure(undefined)}
        >
          {failure.message}
          {failure.correlationId !== undefined && (
            <Text size="xs" c="dimmed" mt={4}>
              {t("errors.correlation", { id: failure.correlationId })}
            </Text>
          )}
        </Alert>
      )}
      {mine.isPending ? (
        <LoadingState height={120} />
      ) : mine.isError ? (
        <Alert color="danger" role="alert" title={t("delegations.failed")}>
          {mine.error instanceof ApiError ? mine.error.message : t("errors.unexpected")}
        </Alert>
      ) : mine.data.data.length === 0 ? (
        <EmptyState
          drawing="nothingYet"
          size="sm"
          title={t("delegations.yours.none")}
          text={t("delegations.yours.noneText")}
        />
      ) : (
        <Stack
          gap="sm"
          component="ul"
          m={0}
          pl={0}
          style={{ listStyle: "none" }}
          aria-label={t("delegations.yours.title")}
        >
          {mine.data.data.map((delegation) => {
            const given = delegation.delegator.id === me?.id;
            const period = periodLabel(delegation.starts_at, delegation.ends_at);
            const duties = dutyList(delegation.duties, i18n.language);
            const canAct = !given && delegation.status === "active" && me?.acting_for?.id !== delegation.delegator.id;
            return (
              <li key={delegation.id} data-delegation={delegation.id}>
                <Group justify="space-between" align="flex-start" wrap="nowrap" gap="md">
                  <Stack gap={2}>
                    <Text size="sm">
                      {given
                        ? t("delegations.yours.given", { name: delegation.delegate.name, duties })
                        : t("delegations.yours.received", { name: delegation.delegator.name, duties })}
                    </Text>
                    <Text size="xs" c="dimmed">
                      {t("delegations.period", period)} {t("delegations.reason", { reason: delegation.reason })}
                    </Text>
                  </Stack>
                  <Group gap="xs" wrap="nowrap">
                    <DelegationStatus delegation={delegation} />
                    {canAct && (
                      <Button
                        size="xs"
                        variant="light"
                        leftSection={<IconUserShare size={14} aria-hidden="true" />}
                        loading={acting === delegation.delegator.id}
                        onClick={() => void actFor(delegation.delegator.id)}
                      >
                        {t("delegations.actFor", { name: delegation.delegator.name })}
                      </Button>
                    )}
                    {isRunning(delegation.status) && (
                      <Button size="xs" variant="default" onClick={() => setEnding(delegation)}>
                        {t("delegations.end.open")}
                      </Button>
                    )}
                  </Group>
                </Group>
              </li>
            );
          })}
        </Stack>
      )}
      <DelegationDialog
        opened={creating}
        forSomeone={false}
        onClose={() => setCreating(false)}
        onDone={async () => {
          setCreating(false);
          await changed();
        }}
      />
      <EndDelegationDialog
        delegation={ending}
        busy={busy}
        onConfirm={() => void end()}
        onCancel={() => setEnding(undefined)}
      />
    </Stack>
  );
}

/** A delegation's status in a meaning and in words. */
export function DelegationStatus({ delegation }: { delegation: Delegation }) {
  const { t } = useTranslation();
  const tone = statusTone[delegation.status];
  return (
    <Badge
      variant={tone === "neutral" ? "default" : "light"}
      {...(tone !== "neutral" ? { color: tone } : {})}
      radius="sm"
    >
      {t(`delegations.status.${delegation.status}`)}
    </Badge>
  );
}

/** Ending a delegation asks first: the delegate stops acting at once. */
export function EndDelegationDialog({
  delegation,
  busy,
  onConfirm,
  onCancel,
}: {
  delegation: Delegation | undefined;
  busy: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const { t } = useTranslation();
  // The words of the last delegation asked about stay while the dialog fades out.
  const [shown, setShown] = useState(delegation);
  if (delegation !== undefined && delegation !== shown) {
    setShown(delegation);
  }
  return (
    <ConfirmDialog
      opened={delegation !== undefined}
      title={t("delegations.end.title")}
      message={
        shown ? t("delegations.end.message", { delegator: shown.delegator.name, delegate: shown.delegate.name }) : ""
      }
      confirmLabel={t("delegations.end.confirm")}
      danger
      busy={busy}
      onConfirm={onConfirm}
      onCancel={onCancel}
    />
  );
}
