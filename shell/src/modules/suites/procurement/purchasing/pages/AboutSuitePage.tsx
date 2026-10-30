import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { Alert, Badge, Group, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle, IconCircleCheck, IconCircleMinus, IconEye } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import type { EntitlementStatus } from "@/api/entitlements";
import { fetchProcurementAbout, type SuiteAbout, type SuiteEntitlement } from "@/api/purchasing";
import { EmptyState, LoadingState, type StatusTone } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { Mandala } from "@/design/patterns";
import { formatDateTime, formatDay } from "@/i18n/dates";

const tones: Record<EntitlementStatus, StatusTone> = { enabled: "success", read_only: "info", disabled: "neutral" };

/** The status in a word, with an icon beside it: the tone is never the only sign (brief section 5). */
function StatusLabel({ status }: { status: EntitlementStatus }) {
  const { t } = useTranslation();
  const tone = tones[status];
  const Icon = status === "enabled" ? IconCircleCheck : status === "read_only" ? IconEye : IconCircleMinus;
  return (
    <Badge
      variant={tone === "neutral" ? "default" : "light"}
      {...(tone !== "neutral" ? { color: tone } : {})}
      radius="sm"
      miw="max-content"
      leftSection={<Icon size={14} aria-hidden="true" />}
    >
      {t(`subscription.statuses.${status}`)}
    </Badge>
  );
}

/** Why it applies as it does, in the subscription page's words: one vocabulary for entitlements wherever they show. */
function reasonOf(entry: SuiteEntitlement, t: TFunction): string {
  return t(`subscription.reasons.${entry.reason}`, {
    defaultValue: entry.reason,
    starts: entry.starts_on === null ? "" : formatDay(entry.starts_on),
    ends: entry.ends_on === null ? "" : formatDay(entry.ends_on),
    grace: entry.grace_ends_on === null ? "" : formatDay(entry.grace_ends_on),
  });
}

/** The release in words: a laptop build says it is one rather than showing "development". */
function release(version: string, t: TFunction): string {
  return version === "development" ? t("suiteAbout.suite.development") : version;
}

function SuiteSection({ about }: { about: SuiteAbout }) {
  const { t } = useTranslation();
  const installation = about.installation;
  return (
    <Stack gap="sm">
      <Group gap="md" wrap="nowrap" align="center">
        <Mandala
          symmetry={6}
          rings={3}
          stroke={2}
          density={0.6}
          seed={`suite-${about.suite}`}
          size={48}
          accent="console"
        />
        <Stack gap={2}>
          <Text size="lg" fw={600}>
            {about.name}
          </Text>
          <Text size="sm" c="dimmed">
            {about.description}
          </Text>
        </Stack>
      </Group>
      <Table variant="vertical" layout="fixed" withTableBorder aria-label={t("suiteAbout.suite.title")}>
        <Table.Tbody>
          <Table.Tr>
            <Table.Th scope="row" w={220}>
              {t("suiteAbout.suite.release")}
            </Table.Th>
            <Table.Td>{release(about.version, t)}</Table.Td>
          </Table.Tr>
          {installation === null ? (
            <Table.Tr>
              <Table.Th scope="row">{t("suiteAbout.suite.installed")}</Table.Th>
              <Table.Td>{t("suiteAbout.suite.notRecorded")}</Table.Td>
            </Table.Tr>
          ) : (
            <>
              <Table.Tr>
                <Table.Th scope="row">{t("suiteAbout.suite.installed")}</Table.Th>
                <Table.Td>
                  {t("suiteAbout.suite.when", {
                    time: formatDateTime(installation.installed_at),
                    version: release(installation.installed_version, t),
                  })}
                  <Job id={installation.installed_by_job} />
                </Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th scope="row">{t("suiteAbout.suite.seeded")}</Table.Th>
                <Table.Td>
                  {t("suiteAbout.suite.when", {
                    time: formatDateTime(installation.seeded_at),
                    version: release(installation.seeded_version, t),
                  })}
                  <Job id={installation.seeded_by_job} />
                </Table.Td>
              </Table.Tr>
            </>
          )}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}

/** The fleet's job that did it, which the operator's provisioning report names too; a laptop's own run has none. */
function Job({ id }: { id: string | null }) {
  const { t } = useTranslation();
  return (
    <Text size="xs" c="dimmed">
      {id === null ? t("suiteAbout.suite.noJob") : t("suiteAbout.suite.job", { id })}
    </Text>
  );
}

function PlanSection({ about }: { about: SuiteAbout }) {
  const { t } = useTranslation();
  if (about.plan === null) {
    return <Text size="sm">{t("suiteAbout.plan.none")}</Text>;
  }
  return (
    <Stack gap="sm">
      <Stack gap={2}>
        <Text size="lg" fw={600}>
          {t(`subscription.plans.${about.plan}`, { defaultValue: about.plan })}
        </Text>
        <Text size="sm" c="dimmed">
          {t("subscription.plan.grace", { count: about.grace_days })}
        </Text>
      </Stack>
      {about.stale && (
        <Alert
          color="warning"
          role="alert"
          icon={<IconAlertTriangle size={18} aria-hidden="true" />}
          title={t("subscription.stale.title")}
        >
          {t("suiteAbout.plan.stale")}
        </Alert>
      )}
    </Stack>
  );
}

/** The suite's modules or features, each with its status in words, why, and until when. */
function EntitlementsTable({
  entries,
  labelledBy,
  kind,
}: {
  entries: SuiteEntitlement[];
  labelledBy: string;
  kind: "module" | "feature";
}) {
  const { t } = useTranslation();
  if (entries.length === 0) {
    return <EmptyState drawing="nothingYet" size="sm" title={t(`suiteAbout.${kind}s.empty`)} />;
  }
  return (
    <Table.ScrollContainer minWidth={560}>
      <Table verticalSpacing="sm" aria-labelledby={labelledBy}>
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">{t(`subscription.columns.${kind}`)}</Table.Th>
            <Table.Th scope="col">{t("subscription.columns.status")}</Table.Th>
            <Table.Th scope="col">{t("subscription.columns.why")}</Table.Th>
            <Table.Th scope="col">{t("subscription.columns.until")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {entries.map((entry) => (
            <Table.Tr key={entry.code}>
              <Table.Th scope="row" fw="normal">
                <Text size="sm" fw={600}>
                  {entry.description ?? t(`subscription.moduleNames.${entry.code}`, { defaultValue: entry.code })}
                </Text>
                <Text size="xs" c="dimmed" ff="monospace">
                  {entry.code}
                </Text>
              </Table.Th>
              <Table.Td>
                <StatusLabel status={entry.status} />
              </Table.Td>
              <Table.Td>{reasonOf(entry, t)}</Table.Td>
              <Table.Td style={{ whiteSpace: "nowrap" }}>
                {entry.ends_on === null ? t("subscription.noEnd") : formatDay(entry.ends_on)}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/**
 * The eProcurement Suite's About page (TF-244, ADR-0059) on the console template: the suite and the release the unit
 * runs, when and by which job it was installed and last updated, the plan the tenant holds for the suite with its grace
 * days, and the suite's modules and features with their entitlement. Read only: suites and plans are the operator's.
 */
export function AboutSuitePage() {
  const { t } = useTranslation();
  const query = useQuery({
    queryKey: ["suites", "procurement", "about"],
    queryFn: ({ signal }) => fetchProcurementAbout(signal),
    staleTime: 30_000,
  });

  if (query.isPending) {
    return <LoadingState height="60vh" />;
  }
  if (query.isError) {
    const failure = query.error instanceof ApiError ? query.error : undefined;
    return (
      <Alert color="danger" role="alert" title={t("suiteAbout.failed")}>
        {failure?.message ?? t("errors.unexpected")}
        {failure?.correlationId !== undefined && (
          <Text size="xs" mt={4}>
            {t("errors.correlation", { id: failure.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }

  const about = query.data;
  return (
    <ConsolePage
      title={t("suiteAbout.title", { suite: about.name })}
      description={t("suiteAbout.description")}
      sections={[
        {
          id: "suite-about-suite",
          title: t("suiteAbout.suite.title"),
          description: t("suiteAbout.suite.description"),
          children: <SuiteSection about={about} />,
        },
        {
          id: "suite-about-plan",
          title: t("suiteAbout.plan.title"),
          description: t("suiteAbout.plan.description"),
          children: <PlanSection about={about} />,
        },
        {
          id: "suite-about-modules",
          title: t("suiteAbout.modules.title"),
          description: t("suiteAbout.modules.description"),
          children: <EntitlementsTable entries={about.modules} labelledBy="suite-about-modules-title" kind="module" />,
        },
        {
          id: "suite-about-features",
          title: t("suiteAbout.features.title"),
          description: t("suiteAbout.features.description"),
          children: (
            <EntitlementsTable entries={about.features} labelledBy="suite-about-features-title" kind="feature" />
          ),
        },
      ]}
    />
  );
}
