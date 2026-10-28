import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { Alert, Badge, Progress, Stack, Table, Text } from "@mantine/core";
import { IconCircleCheck, IconClockHour4, IconRefresh } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { fetchKeys, type KeyRotation, type Keys, type TenantKey } from "@/api/keys";
import { EmptyState, LoadingState } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { formatDateTime, formatDay } from "@/i18n/dates";

const encrypting = new Set(["data", "index"]);

/** A key by its name and what it is for; an unknown one by its code. */
function KeyName({ code }: { code: string }) {
  const { t } = useTranslation();
  return (
    <>
      <Text size="sm" fw={600}>
        {t(`keys.names.${code}`, { defaultValue: code })}
      </Text>
      <Text size="xs" c="dimmed">
        {t(`keys.purposes.${code}`, { defaultValue: "" })}
      </Text>
    </>
  );
}

/** Where a key's latest re-encryption stands, in words beside the bar: the bar is never the only sign (brief section 5). */
function ReencryptionCell({ item }: { item: TenantKey }) {
  const { t } = useTranslation();
  if (!encrypting.has(item.key)) {
    return (
      <Text size="sm" c="dimmed">
        {t("keys.reencryption.notNeeded")}
      </Text>
    );
  }
  const job = item.reencryption;
  if (job === null) {
    return (
      <Text size="sm" c="dimmed">
        {t("keys.reencryption.none")}
      </Text>
    );
  }
  if (job.finished_at !== null) {
    return (
      <Badge
        variant="light"
        color="success"
        radius="sm"
        miw="max-content"
        leftSection={<IconCircleCheck size={14} aria-hidden="true" />}
      >
        {t("keys.reencryption.done", { version: job.to_version })}
      </Badge>
    );
  }
  const percent = job.total === 0 ? 0 : Math.round((job.done / job.total) * 100);
  const words = t("keys.reencryption.progress", { done: job.done, total: job.total, version: job.to_version });
  return (
    <Stack gap={4} miw={180}>
      <Progress value={percent} aria-label={words} size="sm" />
      <Text size="sm">{words}</Text>
    </Stack>
  );
}

function KeysTable({ keys }: { keys: TenantKey[] }) {
  const { t } = useTranslation();
  return (
    <Table.ScrollContainer minWidth={640}>
      <Table verticalSpacing="sm" aria-labelledby="keys-current-title">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">{t("keys.columns.key")}</Table.Th>
            <Table.Th scope="col">{t("keys.columns.version")}</Table.Th>
            <Table.Th scope="col">{t("keys.columns.rotated")}</Table.Th>
            <Table.Th scope="col">{t("keys.columns.next")}</Table.Th>
            <Table.Th scope="col">{t("keys.columns.reencryption")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {keys.map((item) => (
            <Table.Tr key={item.key}>
              <Table.Th scope="row" fw="normal">
                <KeyName code={item.key} />
              </Table.Th>
              <Table.Td>
                <Text size="sm" style={{ whiteSpace: "nowrap" }}>
                  {t("keys.version", { version: item.version })}
                </Text>
                {item.minimum_version < item.version && (
                  <Text size="xs" c="dimmed">
                    {t("keys.olderStillRead", { minimum: item.minimum_version })}
                  </Text>
                )}
              </Table.Td>
              <Table.Td style={{ whiteSpace: "nowrap" }}>
                {item.rotated_at === null ? t("keys.neverRotated") : formatDateTime(item.rotated_at)}
              </Table.Td>
              <Table.Td>
                <Text size="sm" style={{ whiteSpace: "nowrap" }}>
                  {formatDay(item.next_rotation_at)}
                </Text>
                <Text size="xs" c="dimmed">
                  {t("keys.every", { count: item.rotation_days })}
                </Text>
              </Table.Td>
              <Table.Td>
                <ReencryptionCell item={item} />
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/** Where a rotation stands, in words with an icon. */
function stateOf(rotation: KeyRotation, t: TFunction): { words: string; done: boolean } {
  // A password has no versions that keep working: once rotated it is simply changed.
  if (rotation.key === "database") {
    return { words: t("keys.states.changed"), done: true };
  }
  if (rotation.retired_at !== null) {
    return { words: t("keys.states.retired"), done: true };
  }
  if (rotation.key === "signing") {
    return { words: t("keys.states.overlap"), done: false };
  }
  if (rotation.finished_at !== null) {
    return { words: t("keys.states.moved"), done: false };
  }
  return { words: t("keys.states.moving", { done: rotation.done, total: rotation.total }), done: false };
}

function RotationsTable({ rotations }: { rotations: KeyRotation[] }) {
  const { t } = useTranslation();
  if (rotations.length === 0) {
    return (
      <EmptyState
        drawing="nothingYet"
        size="sm"
        title={t("keys.rotations.empty")}
        text={t("keys.rotations.emptyText")}
      />
    );
  }
  return (
    <Table.ScrollContainer minWidth={640}>
      <Table verticalSpacing="sm" aria-labelledby="keys-rotations-title">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">{t("keys.columns.when")}</Table.Th>
            <Table.Th scope="col">{t("keys.columns.key")}</Table.Th>
            <Table.Th scope="col">{t("keys.columns.askedBy")}</Table.Th>
            <Table.Th scope="col">{t("keys.columns.reason")}</Table.Th>
            <Table.Th scope="col">{t("keys.columns.versions")}</Table.Th>
            <Table.Th scope="col">{t("keys.columns.state")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rotations.map((rotation) => {
            const state = stateOf(rotation, t);
            const Icon = state.done ? IconCircleCheck : IconClockHour4;
            return (
              <Table.Tr key={rotation.id}>
                <Table.Td style={{ whiteSpace: "nowrap" }}>{formatDateTime(rotation.rotated_at)}</Table.Td>
                <Table.Th scope="row" fw="normal">
                  {t(`keys.names.${rotation.key}`, { defaultValue: rotation.key })}
                </Table.Th>
                <Table.Td>{rotation.trigger === "schedule" ? t("keys.schedule") : rotation.requested_by}</Table.Td>
                <Table.Td>{rotation.reason}</Table.Td>
                <Table.Td style={{ whiteSpace: "nowrap" }}>
                  {t("keys.fromTo", { from: rotation.from_version, to: rotation.to_version })}
                </Table.Td>
                <Table.Td>
                  <Badge
                    variant={state.done ? "light" : "default"}
                    {...(state.done ? { color: "success" } : {})}
                    radius="sm"
                    miw="max-content"
                    leftSection={<Icon size={14} aria-hidden="true" />}
                  >
                    {state.words}
                  </Badge>
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

function CurrentSection({ data }: { data: Keys }) {
  const { t } = useTranslation();
  if (data.keys.length === 0) {
    return <EmptyState drawing="nothingYet" size="sm" title={t("keys.empty")} text={t("keys.emptyText")} />;
  }
  return (
    <Stack gap="sm">
      <KeysTable keys={data.keys} />
      {data.asked_at !== null && (
        <Text size="xs" c="dimmed">
          <IconRefresh size={12} aria-hidden="true" /> {t("keys.asked", { time: formatDateTime(data.asked_at) })}
        </Text>
      )}
    </Stack>
  );
}

/**
 * The tenant's keys on the console template (TF-074, TF-075, ADR-0054): each key's version, its last and next rotation
 * and the running re-encryption's progress, and the latest rotations with who asked and why. Read only: the platform
 * operator rotates keys, on a schedule and when asked; the tenant sets how often its data and index keys rotate.
 */
export function KeysPage() {
  const { t } = useTranslation();
  const query = useQuery({
    queryKey: ["keys"],
    queryFn: ({ signal }) => fetchKeys(signal),
    // While values move, the progress is read again every few seconds; otherwise twice a minute.
    refetchInterval: (current) =>
      current.state.data?.keys.some((item) => item.reencryption !== null && item.reencryption.finished_at === null)
        ? 5_000
        : 30_000,
  });

  if (query.isPending) {
    return <LoadingState height="60vh" />;
  }
  if (query.isError) {
    const failure = query.error instanceof ApiError ? query.error : undefined;
    return (
      <Alert color="danger" role="alert" title={t("keys.failed")}>
        {failure?.message ?? t("errors.unexpected")}
        {failure?.correlationId !== undefined && (
          <Text size="xs" mt={4}>
            {t("errors.correlation", { id: failure.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }

  return (
    <ConsolePage
      title={t("keys.title")}
      description={t("keys.description")}
      sections={[
        {
          id: "keys-current",
          title: t("keys.current.title"),
          description: t("keys.current.description"),
          children: <CurrentSection data={query.data} />,
        },
        {
          id: "keys-rotations",
          title: t("keys.rotations.title"),
          description: t("keys.rotations.description"),
          children: <RotationsTable rotations={query.data.rotations} />,
        },
      ]}
    />
  );
}
