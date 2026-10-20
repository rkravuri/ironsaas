import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { Alert, Badge, Group, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle, IconCircleCheck, IconCircleMinus, IconEye } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import {
  fetchSubscription,
  type EntitlementStatus,
  type Subscription,
  type SubscriptionFeature,
  type SubscriptionModule,
} from "@/api/entitlements";
import { EmptyState, LoadingState, type StatusTone } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { Mandala } from "@/design/patterns";

const tones: Record<EntitlementStatus, StatusTone> = { enabled: "success", read_only: "info", disabled: "neutral" };

/** A date of the subscription, `yyyy-mm-dd` in UTC, as the person reads dates. */
function day(date: string | null): string | undefined {
  return date === null
    ? undefined
    : new Date(`${date}T00:00:00Z`).toLocaleDateString(undefined, { timeZone: "UTC", dateStyle: "medium" });
}

/** The status in a word, with an icon beside it: the tone is never the only sign (brief section 5). */
function StatusLabel({ status }: { status: EntitlementStatus }) {
  const { t } = useTranslation();
  const tone = tones[status];
  const Icon = status === "enabled" ? IconCircleCheck : status === "read_only" ? IconEye : IconCircleMinus;
  return (
    // A table squeezes a cell to its narrowest; the word is the status, so it is never cut.
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

/** Why it applies as it does, in words: the reason with its dates. */
function reasonOf(
  entry: { reason: string; starts_on: string | null; ends_on: string | null; grace_ends_on: string | null },
  t: TFunction,
): string {
  return t(`subscription.reasons.${entry.reason}`, {
    defaultValue: entry.reason,
    starts: day(entry.starts_on) ?? "",
    ends: day(entry.ends_on) ?? "",
    grace: day(entry.grace_ends_on) ?? "",
  });
}

/** A module by its name for people when the catalogue has one, and by its code always. */
function ModuleName({ code }: { code: string }) {
  const { t } = useTranslation();
  const name = t(`subscription.moduleNames.${code}`, { defaultValue: "" });
  return (
    <>
      {name !== "" && (
        <Text size="sm" fw={600}>
          {name}
        </Text>
      )}
      <Text size={name !== "" ? "xs" : "sm"} {...(name !== "" ? { c: "dimmed" } : {})} ff="monospace">
        {code}
      </Text>
    </>
  );
}

function Until({ entry }: { entry: { ends_on: string | null } }) {
  const { t } = useTranslation();
  return <>{day(entry.ends_on) ?? t("subscription.noEnd")}</>;
}

function ModulesTable({ modules }: { modules: SubscriptionModule[] }) {
  const { t } = useTranslation();
  if (modules.length === 0) {
    return (
      <EmptyState
        drawing="nothingYet"
        size="sm"
        title={t("subscription.modules.empty")}
        text={t("subscription.modules.emptyText")}
      />
    );
  }
  return (
    <Table.ScrollContainer minWidth={640}>
      <Table verticalSpacing="sm" aria-labelledby="subscription-modules-title">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">{t("subscription.columns.module")}</Table.Th>
            <Table.Th scope="col">{t("subscription.columns.sold")}</Table.Th>
            <Table.Th scope="col">{t("subscription.columns.status")}</Table.Th>
            <Table.Th scope="col">{t("subscription.columns.why")}</Table.Th>
            <Table.Th scope="col">{t("subscription.columns.until")}</Table.Th>
            <Table.Th scope="col">{t("subscription.columns.seats")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {modules.map((module) => (
            <Table.Tr key={module.code}>
              <Table.Th scope="row" fw="normal">
                <ModuleName code={module.code} />
              </Table.Th>
              <Table.Td>{t(`subscription.sold.${module.subscription_type}`)}</Table.Td>
              <Table.Td>
                <StatusLabel status={module.status} />
              </Table.Td>
              <Table.Td>{reasonOf(module, t)}</Table.Td>
              <Table.Td style={{ whiteSpace: "nowrap" }}>
                <Until entry={module} />
              </Table.Td>
              <Table.Td>
                {module.seats === null || module.seats.limit === null
                  ? t("subscription.seats.notCounted")
                  : t("subscription.seats.usage", {
                      assigned: module.seats.assigned,
                      limit: module.seats.limit,
                      remaining: module.seats.remaining ?? 0,
                    })}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

function FeaturesTable({ features }: { features: SubscriptionFeature[] }) {
  const { t } = useTranslation();
  if (features.length === 0) {
    return <EmptyState drawing="nothingYet" size="sm" title={t("subscription.features.empty")} />;
  }
  return (
    <Table.ScrollContainer minWidth={640}>
      <Table verticalSpacing="sm" aria-labelledby="subscription-features-title">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">{t("subscription.columns.feature")}</Table.Th>
            <Table.Th scope="col">{t("subscription.columns.status")}</Table.Th>
            <Table.Th scope="col">{t("subscription.columns.why")}</Table.Th>
            <Table.Th scope="col">{t("subscription.columns.until")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {features.map((feature) => (
            <Table.Tr key={feature.code}>
              <Table.Th scope="row" fw="normal">
                <Text size="sm" fw={600}>
                  {feature.description}
                </Text>
                <Text size="xs" c="dimmed" ff="monospace">
                  {feature.code}
                </Text>
              </Table.Th>
              <Table.Td>
                <StatusLabel status={feature.status} />
              </Table.Td>
              <Table.Td>{reasonOf(feature, t)}</Table.Td>
              <Table.Td style={{ whiteSpace: "nowrap" }}>
                <Until entry={feature} />
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

function PlanSection({ subscription }: { subscription: Subscription }) {
  const { t } = useTranslation();
  const plan =
    subscription.plan === null
      ? t("subscription.plan.none")
      : t(`subscription.plans.${subscription.plan}`, { defaultValue: subscription.plan });
  const confirmed =
    subscription.confirmed_at === null
      ? undefined
      : new Date(subscription.confirmed_at).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  return (
    <Stack gap="sm">
      <Group gap="md" wrap="nowrap" align="center">
        <Mandala
          symmetry={8}
          rings={3}
          stroke={2}
          density={0.6}
          seed={`plan-${subscription.plan ?? "none"}`}
          size={48}
          accent="console"
        />
        <Stack gap={2}>
          <Text size="lg" fw={600}>
            {plan}
          </Text>
          <Text size="sm" c="dimmed">
            {t("subscription.plan.grace", { count: subscription.grace_days })}
          </Text>
        </Stack>
      </Group>
      {subscription.stale ? (
        <Alert
          color="warning"
          role="alert"
          icon={<IconAlertTriangle size={18} aria-hidden="true" />}
          title={t("subscription.stale.title")}
        >
          {t("subscription.stale.text", { time: confirmed ?? t("subscription.stale.never") })}
        </Alert>
      ) : (
        confirmed !== undefined && (
          <Text size="sm" c="dimmed">
            {t("subscription.plan.confirmed", { time: confirmed })}
          </Text>
        )
      )}
    </Stack>
  );
}

/**
 * The tenant's subscription on the console template (TF-040, TF-041, TF-045): the plan, the licensed modules and the
 * features with their status, the reason in words, the dates and the seats. Read only: plans are the operator's.
 */
export function SubscriptionPage() {
  const { t } = useTranslation();
  const query = useQuery({
    queryKey: ["subscription"],
    queryFn: ({ signal }) => fetchSubscription(signal),
    staleTime: 30_000,
  });

  if (query.isPending) {
    return <LoadingState height="60vh" />;
  }
  if (query.isError) {
    const failure = query.error instanceof ApiError ? query.error : undefined;
    return (
      <Alert color="danger" role="alert" title={t("subscription.failed")}>
        {failure?.message ?? t("errors.unexpected")}
        {failure?.correlationId !== undefined && (
          <Text size="xs" mt={4}>
            {t("errors.correlation", { id: failure.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }

  const subscription = query.data;
  return (
    <ConsolePage
      title={t("subscription.title")}
      description={t("subscription.description")}
      sections={[
        {
          id: "subscription-plan",
          title: t("subscription.plan.title"),
          description: t("subscription.plan.description"),
          children: <PlanSection subscription={subscription} />,
        },
        {
          id: "subscription-modules",
          title: t("subscription.modules.title"),
          description: t("subscription.modules.description"),
          children: <ModulesTable modules={subscription.modules} />,
        },
        {
          id: "subscription-features",
          title: t("subscription.features.title"),
          description: t("subscription.features.description"),
          children: <FeaturesTable features={subscription.features} />,
        },
      ]}
    />
  );
}
