import { useTranslation } from "react-i18next";
import { Group, Text } from "@mantine/core";
import { IconAlertOctagon, IconAlertTriangle, IconShield, IconShieldHalf } from "@tabler/icons-react";
import type { Risk } from "@/api/authorization";

const icons = { low: IconShield, medium: IconShieldHalf, high: IconAlertTriangle, critical: IconAlertOctagon } as const;

/** A permission's risk as a word and an icon, never a colour alone (brief section 5, TF-030 AC2). */
export function RiskLabel({ risk }: { risk: Risk }) {
  const { t } = useTranslation();
  const Icon = icons[risk];
  return (
    <Group gap={4} wrap="nowrap" component="span" data-risk={risk}>
      <Icon size={14} aria-hidden="true" />
      <Text span size="xs" fw={risk === "high" || risk === "critical" ? 600 : 400}>
        {t(`roles.risk.${risk}`)}
      </Text>
    </Group>
  );
}
