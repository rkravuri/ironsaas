import { useTranslation } from "react-i18next";
import { Alert, List, Text } from "@mantine/core";
import { IconAlertTriangle, IconBan } from "@tabler/icons-react";
import type { GrantConflict } from "@/api/authorization";
import { grantConflictWords } from "./conflict-words";

/**
 * What the conflict rules say about a grant before it is made (TF-036, ADR-0039): the rules that refuse it, then the
 * rules that warn, each a sentence with the rule's reason. The title says which, in words and with its mark, never
 * colour alone; the API refuses a blocked grant whatever this shows. It changes as the grant is chosen, so it is a
 * status, said when the reader pauses, not an alert that interrupts.
 */
export function GrantConflictNotice({ name, conflicts }: { name: string; conflicts: readonly GrantConflict[] }) {
  const { t } = useTranslation();
  const blocks = conflicts.filter((conflict) => conflict.mode === "block");
  const warns = conflicts.filter((conflict) => conflict.mode === "warn");
  const lines = (group: readonly GrantConflict[]) => (
    <List size="sm" spacing={4} mt={4}>
      {group.map((conflict) => (
        <List.Item key={`${conflict.rule_id}:${conflict.with.assignment_id ?? conflict.with.delegation_id ?? ""}:${conflict.unit_id ?? ""}`}>
          {grantConflictWords(t, name, conflict)}
          {conflict.reason !== "" && (
            <Text size="xs" c="dimmed">
              {t("conflicts.preview.reason", { reason: conflict.reason })}
            </Text>
          )}
        </List.Item>
      ))}
    </List>
  );
  return (
    <>
      {blocks.length > 0 && (
        <Alert role="status" color="danger" icon={<IconBan size={18} aria-hidden="true" />} title={t("conflicts.preview.blockTitle")} data-conflict="block">
          <Text size="sm">{t("conflicts.preview.blockHint")}</Text>
          {lines(blocks)}
        </Alert>
      )}
      {warns.length > 0 && (
        <Alert role="status" color="warning" icon={<IconAlertTriangle size={18} aria-hidden="true" />} title={t("conflicts.preview.warnTitle")} data-conflict="warn">
          <Text size="sm">{t("conflicts.preview.warnHint")}</Text>
          {lines(warns)}
        </Alert>
      )}
    </>
  );
}
