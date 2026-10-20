import { useTranslation } from "react-i18next";
import { Badge, Button, Group, Stack, Text, Title } from "@mantine/core";
import type { StatusTone } from "./RecordHeader";

export type ApprovalState = "waiting" | "pending" | "approved" | "rejected";

export interface ApprovalStep {
  id: string;
  name: string;
  approver: string;
  state: ApprovalState;
}

const tones: Record<ApprovalState, StatusTone> = {
  waiting: "neutral",
  pending: "warning",
  approved: "success",
  rejected: "danger",
};

/** The shape of an approval (TF-171): the steps, who decides, where it stands. S5 brings the decisions. */
export function ApprovalPanel({ steps }: { steps: ApprovalStep[] }) {
  const { t } = useTranslation();
  return (
    <Stack gap="sm" data-panel="approval">
      <Title order={4}>{t("approval.title")}</Title>
      <Stack gap="xs">
        {steps.map((step) => {
          const tone = tones[step.state];
          return (
            <Group key={step.id} justify="space-between" wrap="nowrap">
              <Stack gap={0}>
                <Text size="sm" fw={500}>
                  {step.name}
                </Text>
                <Text size="xs" c="dimmed">
                  {step.approver}
                </Text>
              </Stack>
              <Badge
                variant={tone === "neutral" ? "default" : "light"}
                {...(tone !== "neutral" ? { color: tone } : {})}
                radius="sm"
              >
                {t(`approval.states.${step.state}`)}
              </Badge>
            </Group>
          );
        })}
      </Stack>
      <Group gap="sm">
        <Button size="xs" disabled>
          {t("approval.approve")}
        </Button>
        <Button size="xs" variant="default" disabled>
          {t("approval.reject")}
        </Button>
      </Group>
      <Text size="xs" c="dimmed">
        {t("approval.placeholder")}
      </Text>
    </Stack>
  );
}
