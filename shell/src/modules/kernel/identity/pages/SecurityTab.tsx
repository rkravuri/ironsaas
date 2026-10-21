import { useMemo, useState } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Checkbox, PasswordInput, Stack, Table, Text, Title } from "@mantine/core";
import { z } from "zod";
import { ApiError } from "@/api/client";
import { changePassword, fetchSecurityEvents, type SecurityEvent } from "@/api/identity";
import { PasswordRules } from "@/auth/password";
import { failedRule, usePasswordPolicy, type PasswordRule } from "@/auth/password-policy";
import { useSession } from "@/auth/session-context";
import { Form, useZodForm } from "@/components/form";
import { EmptyState, LoadingState } from "@/components/panels";

const when = (value: string) => new Date(value).toLocaleString();

const securityEventsKey = ["identity", "me", "security-events"] as const;

/** My profile's Security tab (TF-012, TF-017, TF-019): change the password, then what happened to the account. */
export function SecurityTab() {
  return (
    <Stack gap="xl">
      <ChangePassword />
      <SecurityActivity />
    </Stack>
  );
}

interface ChangeValues extends Record<string, unknown> {
  currentPassword: string;
  password: string;
  confirm: string;
  endOtherSessions: boolean;
}

/**
 * Change the password (TF-017 AC3): the current one, the new one under the tenant's policy in words, and "End my other
 * sessions", ticked by default, so a person who suspects someone else signed in closes them in the same step. A refusal
 * marks the rule it failed; a wrong current password lands on its field.
 */
function ChangePassword() {
  const { t } = useTranslation();
  const policy = usePasswordPolicy();
  const { me } = useSession();
  const queryClient = useQueryClient();
  const [failed, setFailed] = useState<PasswordRule | undefined>();
  const [done, setDone] = useState<{ endedOthers: boolean } | undefined>();
  const schema = useMemo(
    () =>
      z
        .object({
          currentPassword: z.string().min(1, t("profile.security.currentRequired")),
          password: z.string().min(policy.minimum_length, t("password.tooShort", { min: policy.minimum_length })),
          confirm: z.string(),
          endOtherSessions: z.boolean(),
        })
        .refine((values) => values.confirm === values.password, { path: ["confirm"], message: t("password.mismatch") }),
    [t, policy.minimum_length],
  );
  const form = useZodForm<ChangeValues>({
    schema,
    initialValues: { currentPassword: "", password: "", confirm: "", endOtherSessions: true },
  });

  const submit = async (values: ChangeValues) => {
    setFailed(undefined);
    setDone(undefined);
    try {
      await changePassword(values.currentPassword, values.password, values.endOtherSessions);
    } catch (error) {
      setFailed(failedRule(error));
      throw error;
    }
    form.reset();
    setDone({ endedOthers: values.endOtherSessions });
    void queryClient.invalidateQueries({ queryKey: securityEventsKey });
  };

  const expiresAt = me?.password_expires_at;
  return (
    <Stack gap="sm" maw={560} data-change-password>
      <Title order={3} size="h4">
        {t("profile.security.changeTitle")}
      </Title>
      {expiresAt && (
        <Text size="sm" c="dimmed">
          {t("profile.security.expires", { when: when(expiresAt), days: policy.maximum_age_days })}
        </Text>
      )}
      {done && (
        <Alert color="success" role="status">
          {done.endedOthers ? t("profile.security.changedEnded") : t("profile.security.changed")}
        </Alert>
      )}
      <Form form={form} onSubmit={submit} submitLabel={t("profile.security.changeSubmit")}>
        <PasswordInput
          label={t("profile.security.current")}
          autoComplete="current-password"
          required
          {...form.getInputProps("currentPassword")}
        />
        <PasswordRules policy={policy} withHistory failed={failed} />
        <PasswordInput
          label={t("profile.security.new")}
          autoComplete="new-password"
          required
          {...form.getInputProps("password")}
        />
        <PasswordInput
          label={t("profile.security.confirm")}
          autoComplete="new-password"
          required
          {...form.getInputProps("confirm")}
        />
        <Checkbox
          label={t("profile.security.endOthers")}
          description={t("profile.security.endOthersHint")}
          {...form.getInputProps("endOtherSessions", { type: "checkbox" })}
        />
      </Form>
    </Stack>
  );
}

/**
 * Security activity (TF-019), newest first: what happened to the account, when, and who did it. The person sees their
 * own only; each line is also in the tenant's audit trail. Email of these events arrives with S4.
 */
function SecurityActivity() {
  const { t } = useTranslation();
  const events = useQuery({ queryKey: securityEventsKey, queryFn: ({ signal }) => fetchSecurityEvents(signal) });
  return (
    <Stack gap="sm" data-security-activity>
      <Title order={3} size="h4">
        {t("profile.security.activityTitle")}
      </Title>
      <Text size="sm" c="dimmed" maw={640}>
        {t("profile.security.activityIntro")}
      </Text>
      {events.isPending ? (
        <LoadingState height={120} />
      ) : events.isError ? (
        <Alert color="danger" role="alert" title={t("profile.security.activityFailed")}>
          {events.error instanceof ApiError ? events.error.message : t("errors.unexpected")}
          {events.error instanceof ApiError && events.error.correlationId !== undefined && (
            <Text size="xs" c="dimmed" mt={4}>
              {t("errors.correlation", { id: events.error.correlationId })}
            </Text>
          )}
        </Alert>
      ) : events.data.data.length === 0 ? (
        <EmptyState
          drawing="nothingYet"
          size="sm"
          title={t("profile.security.activityNone")}
          text={t("profile.security.activityNoneText")}
        />
      ) : (
        <Table withTableBorder striped>
          <Table.Caption>{t("profile.security.activityCaption")}</Table.Caption>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t("profile.security.columns.when")}</Table.Th>
              <Table.Th>{t("profile.security.columns.what")}</Table.Th>
              <Table.Th>{t("profile.security.columns.by")}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {events.data.data.map((event) => (
              <Table.Tr key={event.id} data-kind={event.kind}>
                <Table.Td>{when(event.occurred_at)}</Table.Td>
                <Table.Td>
                  {t(`securityEvents.kinds.${event.kind}`, { defaultValue: event.kind })}
                  {event.reason && (
                    <Text size="xs" c="dimmed">
                      {t("securityEvents.reason", { reason: event.reason })}
                    </Text>
                  )}
                </Table.Td>
                <Table.Td>{byWhom(event, t)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Stack>
  );
}

/** Who caused the event, in words: you, an administrator by name, the platform's operator by name, or nobody in particular. */
function byWhom(event: SecurityEvent, t: TFunction): string {
  return t(`securityEvents.actors.${event.actor}`, { name: event.actor_name ?? "", defaultValue: event.actor });
}
