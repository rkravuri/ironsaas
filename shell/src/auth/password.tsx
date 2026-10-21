import { useTranslation } from "react-i18next";
import { List, Text, ThemeIcon } from "@mantine/core";
import { IconAlertTriangle, IconPoint } from "@tabler/icons-react";
import type { PasswordPolicy } from "@/api/identity";
import type { PasswordRule } from "./password-policy";

interface PasswordRulesProps {
  policy: PasswordPolicy;
  /** Whether the person has earlier passwords the history rule compares with: not on an invitation. */
  withHistory: boolean;
  /** The rule the API refused, marked with an icon and words, never by colour alone. */
  failed?: PasswordRule | undefined;
}

/**
 * The policy in words, before the person types (TF-012): the length, the common list, not the login, the history. No
 * rules about kinds of characters: a long passphrase is the advice (NIST SP 800-63B, ADR-0046). After a refusal the
 * failed rule says so.
 */
export function PasswordRules({ policy, withHistory, failed }: PasswordRulesProps) {
  const { t } = useTranslation();
  const rules: { rule: PasswordRule; text: string }[] = [
    { rule: "too_short", text: t("password.rules.length", { min: policy.minimum_length }) },
    ...(policy.common_check ? [{ rule: "common" as const, text: t("password.rules.common") }] : []),
    { rule: "is_login_id", text: t("password.rules.notLogin") },
    ...(withHistory && policy.history_depth > 0
      ? [{ rule: "reused" as const, text: t("password.rules.history", { count: policy.history_depth }) }]
      : []),
    ...(failed === "too_long"
      ? [{ rule: "too_long" as const, text: t("password.rules.tooLong", { max: policy.maximum_length }) }]
      : []),
  ];
  return (
    <div data-password-rules>
      <Text size="sm" fw={600} id="password-rules-title">
        {t("password.rules.title")}
      </Text>
      <List
        size="sm"
        spacing={4}
        mt={4}
        aria-labelledby="password-rules-title"
        icon={<IconPoint size={14} aria-hidden="true" />}
      >
        {rules.map(({ rule, text }) => (
          <List.Item
            key={rule}
            data-rule={rule}
            data-failed={failed === rule ? "true" : undefined}
            {...(failed === rule
              ? {
                  icon: (
                    <ThemeIcon size={18} radius="xl" color="danger" variant="light">
                      <IconAlertTriangle size={12} aria-hidden="true" />
                    </ThemeIcon>
                  ),
                }
              : {})}
          >
            {failed === rule ? (
              <Text span size="sm" fw={600}>
                {text} {t("password.rules.failed")}
              </Text>
            ) : (
              text
            )}
          </List.Item>
        ))}
      </List>
      <Text size="xs" c="dimmed" mt={6}>
        {t("password.rules.advice")}
      </Text>
    </div>
  );
}
