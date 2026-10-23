import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Anchor, Button, Group, Stack, Text } from "@mantine/core";
import { IconArrowBackUp } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { changesQueryKey, fetchChanges, withdrawChange, type PendingChange } from "@/api/changes";
import { ConfirmDialog, EmptyState, LoadingState } from "@/components/panels";
import { formatDateTime } from "@/i18n/dates";
import { areaWords } from "./change-words";

const mineKey = [...changesQueryKey, "mine", "pending"] as const;

/**
 * My pending changes, in My profile for everyone (TF-068, ADR-0052, at T8's review): the person's own changes that wait
 * for a second administrator, each with its area and how long it waits, a link to its page, and Withdraw. The console's
 * list is the approvers'; this one needs no permission, since a person always sees their own.
 */
export function MyPendingChanges() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const mine = useQuery({
    queryKey: mineKey,
    queryFn: ({ signal }) => fetchChanges("mine", "status=pending&page_size=100", signal),
  });
  const [withdrawing, setWithdrawing] = useState<PendingChange | undefined>();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();

  const withdraw = async () => {
    if (!withdrawing) {
      return;
    }
    setBusy(true);
    setFailure(undefined);
    try {
      await withdrawChange(withdrawing.id);
      await queryClient.invalidateQueries({ queryKey: changesQueryKey });
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
      setWithdrawing(undefined);
    }
  };

  return (
    <Stack gap="md">
      <Text size="sm">{t("changes.mine.intro")}</Text>
      {failure && (
        <Alert color="danger" role="alert">
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
        <Alert color="danger" role="alert" title={t("changes.mine.failed")}>
          {mine.error instanceof ApiError ? mine.error.message : t("errors.unexpected")}
        </Alert>
      ) : mine.data.data.length === 0 ? (
        <EmptyState drawing="nothingYet" size="sm" title={t("changes.mine.none")} text={t("changes.mine.noneText")} />
      ) : (
        <Stack gap="sm" component="ul" m={0} pl={0} style={{ listStyle: "none" }} aria-label={t("changes.mine.title")}>
          {mine.data.data.map((change) => (
            <li key={change.id} data-pending-change={change.id}>
              <Group justify="space-between" align="flex-start" wrap="nowrap" gap="md">
                <Stack gap={2}>
                  <Anchor component={Link} to={`/console/changes/${change.id}`} size="sm" fw={600}>
                    {change.title}
                  </Anchor>
                  <Text size="xs" c="dimmed">
                    {t("changes.mine.facts", {
                      area: areaWords(change.area, t),
                      at: formatDateTime(change.requested_at),
                      until: formatDateTime(change.expires_at),
                    })}
                  </Text>
                </Stack>
                <Button
                  size="xs"
                  variant="default"
                  leftSection={<IconArrowBackUp size={14} aria-hidden="true" />}
                  aria-label={t("changes.mine.withdrawLabel", { title: change.title })}
                  onClick={() => setWithdrawing(change)}
                >
                  {t("changes.withdraw.open")}
                </Button>
              </Group>
            </li>
          ))}
        </Stack>
      )}
      <ConfirmDialog
        opened={withdrawing !== undefined}
        title={t("changes.withdraw.title")}
        message={t("changes.withdraw.message", { title: withdrawing?.title ?? "" })}
        confirmLabel={t("changes.withdraw.confirm")}
        cancelLabel={t("changes.withdraw.keep")}
        busy={busy}
        onCancel={() => setWithdrawing(undefined)}
        onConfirm={() => void withdraw()}
      />
    </Stack>
  );
}
