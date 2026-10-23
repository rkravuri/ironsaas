import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Alert, Badge, Code, Group, SimpleGrid, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle, IconCircleCheck, IconCircleX, IconHistory } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { auditQueryKey, fetchAuditChain, type ChainVerification } from "@/api/audit";
import { EmptyState, LoadingState } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";

const results = {
  passed: { color: "success", icon: IconCircleCheck },
  restored: { color: "warning", icon: IconHistory },
  failed: { color: "danger", icon: IconCircleX },
} as const;

/** A verification's result: its icon and its word, the colour only beside them. */
function ResultBadge({ result }: { result: string }) {
  const { t } = useTranslation();
  const shown = results[result as keyof typeof results] ?? { color: "warning", icon: IconAlertTriangle };
  const Icon = shown.icon;
  return (
    <Badge
      variant="light"
      color={shown.color}
      size="lg"
      leftSection={<Icon size={14} aria-hidden="true" />}
      data-verification-result={result}
    >
      {t(`audit.chain.results.${result}`, { defaultValue: result })}
    </Badge>
  );
}

/** One figure of the chain, with its label above it. */
function Figure({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Stack gap={2}>
      <Text size="xs" c="dimmed">
        {label}
      </Text>
      <Text size="sm" fw={600} component="div">
        {children}
      </Text>
    </Stack>
  );
}

/** What the last verification found and, after a restore, where the chain is whole up to. */
function Verification({ verification }: { verification: ChainVerification }) {
  const { t } = useTranslation();
  return (
    <Stack gap="md">
      <Group gap="md" align="center">
        <ResultBadge result={verification.result} />
        <Text size="sm">
          {t(verification.full ? "audit.chain.fullRun" : "audit.chain.dailyRun", {
            when: formatDateTime(verification.verified_at),
          })}
        </Text>
      </Group>
      <Text size="sm">
        {t("audit.chain.checked", {
          count: verification.events_checked,
          from: verification.from_position,
          to: verification.to_position,
        })}
      </Text>
      {verification.restore_position !== null && (
        <Alert color="warning" role="status" title={t("audit.chain.restoreTitle")} data-restore-point>
          {t("audit.chain.restoreText", {
            position: verification.restore_position,
            when: verification.restore_at ? formatDateTime(verification.restore_at) : "",
          })}
        </Alert>
      )}
      {verification.problems.length > 0 && (
        <Table withTableBorder striped data-chain-problems>
          <Table.Caption>{t("audit.chain.problemsCaption", { count: verification.problems.length })}</Table.Caption>
          <Table.Thead>
            <Table.Tr>
              <Table.Th ta="right">{t("audit.columns.position")}</Table.Th>
              <Table.Th>{t("audit.chain.problem")}</Table.Th>
              <Table.Th>{t("audit.chain.eventId")}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {verification.problems.map((problem) => (
              <Table.Tr key={`${problem.kind}:${problem.position}`}>
                <Table.Td ta="right">{problem.position}</Table.Td>
                <Table.Td>{t(`audit.chain.problems.${problem.kind}`, { defaultValue: problem.message })}</Table.Td>
                <Table.Td>{problem.event_id ? <Code>{problem.event_id}</Code> : t("audit.chain.noEvent")}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Stack>
  );
}

/**
 * The audit chain's status (TF-061, ADR-0050): the fleet's last verification of this tenant's chain, its result and
 * what it found, the restore point after a restore; then where the chain stands here (its head, what waits for a seal,
 * what moved to the archive or was pruned, how far the control plane holds anchors), and the latest seals.
 */
export function AuditChainPage() {
  const { t } = useTranslation();
  const chain = useQuery({ queryKey: [...auditQueryKey, "chain"], queryFn: ({ signal }) => fetchAuditChain(signal) });

  if (chain.isPending) {
    return <LoadingState height={240} />;
  }

  if (chain.isError) {
    return (
      <Alert color="danger" role="alert" title={t("audit.chain.failed")}>
        {chain.error instanceof ApiError ? chain.error.message : t("errors.unexpected")}
        {chain.error instanceof ApiError && chain.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: chain.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }

  const status = chain.data;
  return (
    <ConsolePage
      title={t("audit.chain.title")}
      description={t("audit.chain.description")}
      sections={[
        {
          id: "verification",
          title: t("audit.chain.verificationTitle"),
          description: t("audit.chain.verificationDescription"),
          children: status.latest_verification ? (
            <Verification verification={status.latest_verification} />
          ) : (
            <EmptyState
              drawing="nothingYet"
              size="sm"
              title={t("audit.chain.noVerification")}
              text={t("audit.chain.noVerificationText")}
            />
          ),
        },
        {
          id: "chain",
          title: t("audit.chain.chainTitle"),
          description: t("audit.chain.chainDescription"),
          children: (
            <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} spacing="lg" data-chain-figures>
              <Figure label={t("audit.chain.head")}>
                {status.head_position === null ? t("audit.chain.empty") : status.head_position}
              </Figure>
              <Figure label={t("audit.chain.sealedAt")}>
                {status.head_sealed_at ? formatDateTime(status.head_sealed_at) : t("audit.chain.never")}
              </Figure>
              <Figure label={t("audit.chain.unsealed")}>{status.unsealed}</Figure>
              <Figure label={t("audit.chain.anchoredUpTo")}>
                {status.anchored_up_to === null
                  ? t("audit.chain.notAnchored")
                  : t(status.anchored ? "audit.chain.anchoredAll" : "audit.chain.anchoredBehind", {
                      position: status.anchored_up_to,
                    })}
              </Figure>
              <Figure label={t("audit.chain.archivedUpTo")}>
                {status.archived_up_to === 0 ? t("audit.chain.none") : status.archived_up_to}
              </Figure>
              <Figure label={t("audit.chain.prunedUpTo")}>
                {status.pruned_up_to === 0 ? t("audit.chain.none") : status.pruned_up_to}
              </Figure>
              {status.head_hash && (
                <Stack gap={2} style={{ gridColumn: "1 / -1" }}>
                  <Text size="xs" c="dimmed">
                    {t("audit.chain.headHash")}
                  </Text>
                  <Code block style={{ overflowWrap: "anywhere", whiteSpace: "normal" }}>
                    {status.head_hash}
                  </Code>
                </Stack>
              )}
            </SimpleGrid>
          ),
        },
        {
          id: "seals",
          title: t("audit.chain.sealsTitle"),
          description: t("audit.chain.sealsDescription"),
          children:
            status.seals.length === 0 ? (
              <Text size="sm" c="dimmed">
                {t("audit.chain.noSeals")}
              </Text>
            ) : (
              <Table withTableBorder striped data-chain-seals>
                <Table.Caption>{t("audit.chain.sealsCaption", { count: status.seals.length })}</Table.Caption>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t("audit.chain.sealedAtColumn")}</Table.Th>
                    <Table.Th ta="right">{t("audit.chain.positions")}</Table.Th>
                    <Table.Th>{t("audit.chain.lastHash")}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {status.seals.map((seal) => (
                    <Table.Tr key={seal.last_position}>
                      <Table.Td>{formatDateTime(seal.sealed_at)}</Table.Td>
                      <Table.Td ta="right">
                        {t("audit.chain.range", { first: seal.first_position, last: seal.last_position })}
                      </Table.Td>
                      <Table.Td>
                        <Code title={seal.last_hash}>{seal.last_hash.slice(0, 16)}</Code>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            ),
        },
      ]}
    />
  );
}
