import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, NumberInput, Stack, Table, Text, Textarea } from "@mantine/core";
import { z } from "zod";
import { ApiError } from "@/api/client";
import { auditQueryKey, chooseRetention, fetchRetention, type RetentionClass } from "@/api/audit";
import { useSession } from "@/auth/session-context";
import { Form, useZodForm } from "@/components/form";
import { FormDialog, LoadingState } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { periodWords, retentionClassName } from "./audit-words";

export const manageRetention = "audit.retention.manage";
const retentionKey = [...auditQueryKey, "retention"] as const;

interface ChoiceValues extends Record<string, unknown> {
  days: number;
  reason: string;
}

/**
 * Retention (TF-066, ADR-0050): how long each class of records is kept before the retention job archives and deletes
 * it, within the limits the operator set; and how long the audit trail stays in the database before the archive. A
 * holder of audit.retention.manage chooses a class's days, with a reason and a recent sign-in; below the operator's
 * minimum is refused, and the page shows why. A legal hold stops a deletion whatever the class says.
 */
export function RetentionPage() {
  const { t } = useTranslation();
  const session = useSession();
  const queryClient = useQueryClient();
  const canManage = session.permissions.includes(manageRetention);
  const classes = useQuery({ queryKey: retentionKey, queryFn: ({ signal }) => fetchRetention(signal) });
  const [choosing, setChoosing] = useState<RetentionClass | undefined>();
  const [done, setDone] = useState<string | undefined>();

  const rows = classes.data?.classes ?? [];
  return (
    <>
      <ConsolePage
        title={t("retention.title")}
        description={t("retention.description")}
        sections={[
          {
            id: "classes",
            title: t("retention.classesTitle"),
            description: t("retention.classesDescription"),
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
                {classes.isPending ? (
                  <LoadingState height={160} />
                ) : classes.isError ? (
                  <Alert color="danger" role="alert">
                    {classes.error instanceof ApiError ? classes.error.message : t("errors.unexpected")}
                    {classes.error instanceof ApiError && classes.error.correlationId !== undefined && (
                      <Text size="xs" c="dimmed" mt={4}>
                        {t("errors.correlation", { id: classes.error.correlationId })}
                      </Text>
                    )}
                  </Alert>
                ) : (
                  <Table withTableBorder striped data-retention-classes>
                    <Table.Caption>{t("retention.caption")}</Table.Caption>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>{t("retention.columns.class")}</Table.Th>
                        <Table.Th>{t("retention.columns.kept")}</Table.Th>
                        <Table.Th>{t("retention.columns.limits")}</Table.Th>
                        <Table.Th>{t("retention.columns.records")}</Table.Th>
                        {canManage && <Table.Th>{t("retention.columns.actions")}</Table.Th>}
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {rows.map((row) => (
                        <Table.Tr key={row.code} data-retention-class={row.code}>
                          <Table.Td>
                            <Text size="sm" fw={600}>
                              {retentionClassName(row.code, t)}
                            </Text>
                            <Text size="xs" c="dimmed">
                              {t(`retention.classes.${row.code}.text`, { defaultValue: row.description })}
                            </Text>
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm" fw={row.chosen ? 600 : undefined}>
                              {periodWords(row.days, t)}
                            </Text>
                            <Text size="xs" c="dimmed">
                              {t(row.chosen ? "retention.chosen" : "retention.byDefault")}
                            </Text>
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm">
                              {t("retention.limits", {
                                minimum: periodWords(row.minimum_days, t),
                                maximum: periodWords(row.maximum_days, t),
                              })}
                            </Text>
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm">
                              {row.record_types.length === 0
                                ? t("retention.noRecords")
                                : row.record_types
                                    .map((type) => t(`retention.recordTypes.${type}`, { defaultValue: type }))
                                    .join(", ")}
                            </Text>
                          </Table.Td>
                          {canManage && (
                            <Table.Td>
                              <Button
                                variant="subtle"
                                size="compact-sm"
                                onClick={() => setChoosing(row)}
                                aria-label={t("retention.changeNamed", { name: retentionClassName(row.code, t) })}
                              >
                                {t("retention.change")}
                              </Button>
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
      {choosing && (
        <ChoiceDialog
          retention={choosing}
          onClose={() => setChoosing(undefined)}
          onChosen={async (chosen) => {
            setChoosing(undefined);
            setDone(
              t("retention.saved", { name: retentionClassName(chosen.code, t), period: periodWords(chosen.days, t) }),
            );
            await queryClient.invalidateQueries({ queryKey: retentionKey });
          }}
        />
      )}
    </>
  );
}

/**
 * A class's days, with a reason. The form holds the days to the operator's limits; the API checks them again and, below
 * the minimum, answers why at the top of the form.
 */
function ChoiceDialog({
  retention,
  onClose,
  onChosen,
}: {
  retention: RetentionClass;
  onClose: () => void;
  onChosen: (chosen: RetentionClass) => Promise<void>;
}) {
  const { t } = useTranslation();
  const name = retentionClassName(retention.code, t);
  const schema = useMemo(
    () =>
      z.object({
        days: z
          .number({ message: t("retention.form.daysRequired") })
          .int(t("retention.form.daysWhole"))
          .min(retention.minimum_days, t("retention.form.belowMinimum", { days: retention.minimum_days }))
          .max(retention.maximum_days, t("retention.form.aboveMaximum", { days: retention.maximum_days })),
        reason: z.string().trim().min(1, t("reason.required")).max(500),
      }),
    [t, retention],
  );
  const form = useZodForm<ChoiceValues>({ schema, initialValues: { days: retention.days, reason: "" } });
  const submit = async (values: ChoiceValues) => {
    const chosen = await chooseRetention(retention.code, values.days, values.reason.trim());
    await onChosen(chosen);
  };
  return (
    <FormDialog opened title={t("retention.form.title", { name })} onClose={onClose}>
      <Form
        form={form}
        onSubmit={submit}
        submitLabel={t("retention.form.confirm")}
        secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      >
        <Text size="sm">
          {t("retention.form.message", {
            minimum: periodWords(retention.minimum_days, t),
            maximum: periodWords(retention.maximum_days, t),
            standard: periodWords(retention.default_days, t),
          })}
        </Text>
        <NumberInput
          label={t("retention.form.days")}
          description={t("retention.form.daysHint")}
          required
          allowDecimal={false}
          allowNegative={false}
          {...form.getInputProps("days")}
        />
        <Textarea
          label={t("retention.form.reason")}
          description={t("retention.form.reasonHint")}
          required
          autosize
          minRows={2}
          maxLength={500}
          {...form.getInputProps("reason")}
        />
      </Form>
    </FormDialog>
  );
}
