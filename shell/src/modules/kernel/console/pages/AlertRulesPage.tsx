import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Badge, Button, Code, NumberInput, Stack, Table, Text } from "@mantine/core";
import { z } from "zod";
import { ApiError } from "@/api/client";
import { fetchAlertRules, setAlertRule, type AlertRule } from "@/api/monitoring";
import { useSession } from "@/auth/session-context";
import { Form, useZodForm } from "@/components/form";
import { FormDialog, LoadingState } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";
import { manageRules, thresholdWords } from "./alert-words";

const rulesKey = ["alert-rules"] as const;

/**
 * The alert rules (TF-079, ADR-0056): each with what it counts, the platform operator's values, what runs here and
 * whether it runs or fails, and why. A holder of monitoring.rules.manage makes a rule stricter, within its bounds, or
 * returns it to the operator's values; looser is refused. A link from a failing rule's alert names the rule.
 */
export function AlertRulesPage() {
  const { t } = useTranslation();
  const session = useSession();
  const queryClient = useQueryClient();
  const [parameters] = useSearchParams();
  const canManage = session.permissions.includes(manageRules);
  const rules = useQuery({
    queryKey: rulesKey,
    queryFn: ({ signal }) => fetchAlertRules(signal),
    refetchInterval: 60_000,
  });
  const [editing, setEditing] = useState<AlertRule | undefined>();
  const [done, setDone] = useState<string | undefined>();
  const named = parameters.get("rule");
  const pointed = rules.data?.data.find((rule) => rule.name === named);

  return (
    <>
      <ConsolePage
        title={t("alertRules.title")}
        description={t("alertRules.description")}
        sections={[
          {
            id: "rules",
            title: t("alertRules.sectionTitle"),
            description: rules.data?.evaluated_at
              ? t("alertRules.evaluatedAt", { at: formatDateTime(rules.data.evaluated_at) })
              : t("alertRules.neverEvaluated"),
            children: (
              <Stack gap="sm">
                {done && (
                  <Alert
                    color="success"
                    role="status"
                    withCloseButton
                    closeButtonLabel={t("dialog.close")}
                    onClose={() => setDone(undefined)}
                  >
                    {done}
                  </Alert>
                )}
                {pointed && (
                  <Alert color={pointed.state === "failing" ? "danger" : "info"} role="status" data-rule-pointed>
                    {pointed.state === "failing"
                      ? t("alertRules.pointedFailing", { title: pointed.title, reason: pointed.reason ?? "" })
                      : t("alertRules.pointedRunning", { title: pointed.title })}
                  </Alert>
                )}
                {rules.isPending ? (
                  <LoadingState height={200} />
                ) : rules.isError ? (
                  <Alert color="danger" role="alert">
                    {rules.error instanceof ApiError ? rules.error.message : t("errors.unexpected")}
                    {rules.error instanceof ApiError && rules.error.correlationId !== undefined && (
                      <Text size="xs" c="dimmed" mt={4}>
                        {t("errors.correlation", { id: rules.error.correlationId })}
                      </Text>
                    )}
                  </Alert>
                ) : (
                  <Table withTableBorder striped data-alert-rules>
                    <Table.Caption>{t("alertRules.caption")}</Table.Caption>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>{t("alertRules.columns.rule")}</Table.Th>
                        <Table.Th>{t("alertRules.columns.runs")}</Table.Th>
                        <Table.Th>{t("alertRules.columns.operator")}</Table.Th>
                        <Table.Th>{t("alertRules.columns.state")}</Table.Th>
                        {canManage && <Table.Th>{t("alertRules.columns.actions")}</Table.Th>}
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {rules.data.data.map((rule) => (
                        <Table.Tr key={rule.name} data-alert-rule={rule.name}>
                          <Table.Td>
                            <Text size="sm" fw={600}>
                              {rule.title}
                            </Text>
                            <Text size="xs" c="dimmed">
                              {t("alertRules.counts", {
                                types: rule.event_types.length,
                                group: t(`alertRules.groups.${rule.group_by}`),
                              })}{" "}
                              <Code>{rule.name}</Code>
                            </Text>
                          </Table.Td>
                          <Table.Td>
                            {rule.effective ? (
                              <>
                                <Text
                                  size="sm"
                                  fw={
                                    rule.tenant.threshold != null ||
                                    rule.tenant.window_minutes != null ||
                                    rule.tenant.above != null
                                      ? 600
                                      : undefined
                                  }
                                >
                                  {thresholdWords(rule, rule.effective, t)}
                                </Text>
                                <Text size="xs" c="dimmed">
                                  {t(`securityAlerts.severities.${rule.effective.severity}`, {
                                    defaultValue: rule.effective.severity,
                                  })}
                                  {", "}
                                  {rule.tenant.threshold != null ||
                                  rule.tenant.window_minutes != null ||
                                  rule.tenant.above != null
                                    ? t("alertRules.stricter")
                                    : t("alertRules.byOperator")}
                                </Text>
                              </>
                            ) : (
                              <Text size="sm">{t("alertRules.cannotRun")}</Text>
                            )}
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm">{rule.operator ? thresholdWords(rule, rule.operator, t) : ""}</Text>
                          </Table.Td>
                          <Table.Td>
                            <Badge variant="light" color={rule.state === "failing" ? "danger" : "success"}>
                              {t(`alertRules.states.${rule.state}`)}
                            </Badge>
                            {rule.state === "failing" && rule.reason && (
                              <Text size="xs" mt={4} maw={360}>
                                {rule.reason}
                              </Text>
                            )}
                            <Text size="xs" c="dimmed" mt={2}>
                              {rule.last_run_at
                                ? t("alertRules.lastRun", { at: formatDateTime(rule.last_run_at) })
                                : t("alertRules.notRunYet")}
                            </Text>
                          </Table.Td>
                          {canManage && (
                            <Table.Td>
                              {rule.bounds && (rule.bounds.threshold_editable || rule.bounds.above_editable) ? (
                                <Button
                                  variant="subtle"
                                  size="compact-sm"
                                  onClick={() => setEditing(rule)}
                                  aria-label={t("alertRules.changeNamed", { title: rule.title })}
                                >
                                  {t("alertRules.change")}
                                </Button>
                              ) : (
                                <Text size="xs" c="dimmed">
                                  {rule.at_once ? t("alertRules.fixed") : ""}
                                </Text>
                              )}
                            </Table.Td>
                          )}
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                )}
              </Stack>
            ),
          },
        ]}
      />
      {editing && (
        <RuleDialog
          rule={editing}
          onClose={() => setEditing(undefined)}
          onSaved={async (saved) => {
            setEditing(undefined);
            setDone(
              t("alertRules.saved", {
                title: saved.title,
                values: saved.effective ? thresholdWords(saved, saved.effective, t) : "",
              }),
            );
            await queryClient.invalidateQueries({ queryKey: rulesKey });
          }}
        />
      )}
    </>
  );
}

interface RuleValues extends Record<string, unknown> {
  threshold: number | "";
  window: number | "";
  above: number | "";
}

/**
 * A rule's stricter values. The form holds each to the rule's bounds; the API checks them again and answers a looser one
 * at the top of the form. An empty value keeps the operator's.
 */
function RuleDialog({
  rule,
  onClose,
  onSaved,
}: {
  rule: AlertRule;
  onClose: () => void;
  onSaved: (saved: AlertRule) => Promise<void>;
}) {
  const { t } = useTranslation();
  const bounds = rule.bounds!;
  const operator = rule.operator!;
  const schema = useMemo(() => {
    const within = (minimum: number, maximum: number, message: string) =>
      z.union([z.literal(""), z.number().int(t("alertRules.form.whole")).min(minimum, message).max(maximum, message)]);
    return z.object({
      threshold: within(
        bounds.threshold_minimum,
        bounds.threshold_maximum,
        t("alertRules.form.thresholdBounds", { minimum: bounds.threshold_minimum, maximum: bounds.threshold_maximum }),
      ),
      window: within(
        bounds.window_minimum,
        bounds.window_maximum,
        t("alertRules.form.windowBounds", { minimum: bounds.window_minimum, maximum: bounds.window_maximum }),
      ),
      above: within(
        bounds.above_minimum ?? 1,
        bounds.above_maximum ?? 1,
        t("alertRules.form.aboveBounds", { minimum: bounds.above_minimum ?? 1, maximum: bounds.above_maximum ?? 1 }),
      ),
    });
  }, [t, bounds]);
  const form = useZodForm<RuleValues>({
    schema,
    initialValues: {
      threshold: rule.tenant.threshold ?? "",
      window: rule.tenant.window_minutes ?? "",
      above: rule.tenant.above ?? "",
    },
  });
  const value = (entered: number | "") => (entered === "" ? null : entered);
  const save = async (values: RuleValues) => {
    const saved = await setAlertRule(rule.name, {
      threshold: bounds.threshold_editable ? value(values.threshold) : null,
      window_minutes: bounds.threshold_editable ? value(values.window) : null,
      above: bounds.above_editable ? value(values.above) : null,
    });
    await onSaved(saved);
  };
  const reset = async () => {
    const saved = await setAlertRule(rule.name, { threshold: null, window_minutes: null, above: null });
    await onSaved(saved);
  };
  return (
    <FormDialog opened title={t("alertRules.form.title", { title: rule.title })} onClose={onClose}>
      <Form
        form={form}
        onSubmit={save}
        submitLabel={t("alertRules.form.save")}
        secondary={{ label: t("alertRules.form.reset"), onClick: () => void reset() }}
      >
        <Text size="sm">{t("alertRules.form.message", { values: thresholdWords(rule, operator, t) })}</Text>
        {bounds.threshold_editable && (
          <>
            <NumberInput
              label={t("alertRules.form.threshold")}
              description={t("alertRules.form.thresholdHint", { maximum: bounds.threshold_maximum })}
              placeholder={String(operator.threshold)}
              allowDecimal={false}
              allowNegative={false}
              {...form.getInputProps("threshold")}
            />
            <NumberInput
              label={t("alertRules.form.window")}
              description={t("alertRules.form.windowHint", { minimum: bounds.window_minimum })}
              placeholder={String(operator.window_minutes)}
              allowDecimal={false}
              allowNegative={false}
              {...form.getInputProps("window")}
            />
          </>
        )}
        {bounds.above_editable && (
          <NumberInput
            label={t("alertRules.form.above")}
            description={t("alertRules.form.aboveHint", { maximum: bounds.above_maximum ?? 0 })}
            placeholder={String(operator.above ?? "")}
            allowDecimal={false}
            allowNegative={false}
            {...form.getInputProps("above")}
          />
        )}
      </Form>
    </FormDialog>
  );
}
