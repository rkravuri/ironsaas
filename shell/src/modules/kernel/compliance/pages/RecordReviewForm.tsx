import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import {
  ActionIcon,
  Button,
  Group,
  MultiSelect,
  SegmentedControl,
  Stack,
  Text,
  TextInput,
  Textarea,
} from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { z } from "zod";
import { fetchAuditEventTypes } from "@/api/audit";
import { fetchUnits } from "@/api/authorization";
import { auditReviewOutcomes, type AuditReviewInput, type AuditReviewOutcome } from "@/api/compliance";
import { Form, useZodForm } from "@/components/form";
import { today } from "@/modules/kernel/console/pages/delegation-words";

const notesLimit = 4000;
const searchesLimit = 20;
const firstOutcome: AuditReviewOutcome = "no_findings";

/**
 * What a review records (TF-219 AC3): the period, the scope (units and event types, none meaning all), the outcome, its
 * notes, and the searches the reviewer ran, each a link copied from the audit trail's page. Continuing leads to the
 * signature; nothing is saved before it.
 */
export function RecordReviewForm({
  onClose,
  onContinue,
}: {
  onClose: () => void;
  onContinue: (input: AuditReviewInput) => void;
}) {
  const { t } = useTranslation();
  const last = today();
  const units = useQuery({ queryKey: ["authz", "units"], queryFn: ({ signal }) => fetchUnits(signal) });
  const types = useQuery({ queryKey: ["audit", "types"], queryFn: ({ signal }) => fetchAuditEventTypes(signal) });
  const schema = useMemo(
    () =>
      z
        .object({
          period_from: z.string().min(1, t("auditReviews.form.fromRequired")),
          period_to: z
            .string()
            .min(1, t("auditReviews.form.toRequired"))
            .refine((value) => value <= last, t("auditReviews.form.toFuture")),
          unit_ids: z.array(z.string()),
          event_types: z.array(z.string()),
          outcome: z.enum(auditReviewOutcomes),
          notes: z.string().max(notesLimit, t("auditReviews.form.notesTooLong", { count: notesLimit })),
          searches: z
            .array(
              z.object({
                label: z.string().trim().min(1, t("auditReviews.form.searchLabelRequired")),
                link: z
                  .string()
                  .trim()
                  .refine(
                    (value) => value === "/audit" || value.startsWith("/audit?"),
                    t("auditReviews.form.searchLink"),
                  ),
              }),
            )
            .max(searchesLimit),
        })
        .refine(
          (values) => values.period_to === "" || values.period_from === "" || values.period_to >= values.period_from,
          {
            path: ["period_to"],
            message: t("auditReviews.form.toBeforeFrom"),
          },
        )
        .refine((values) => values.outcome === "no_findings" || values.notes.trim() !== "", {
          path: ["notes"],
          message: t("auditReviews.form.notesRequired"),
        }),
    [last, t],
  );
  const form = useZodForm({
    schema,
    initialValues: {
      period_from: "",
      period_to: "",
      unit_ids: [] as string[],
      event_types: [] as string[],
      outcome: firstOutcome,
      notes: "",
      searches: [] as { label: string; link: string }[],
    },
  });

  return (
    <Form
      form={form}
      onSubmit={(values) =>
        Promise.resolve(
          onContinue({
            period_from: values.period_from,
            period_to: values.period_to,
            unit_ids: values.unit_ids,
            event_types: values.event_types,
            outcome: values.outcome,
            notes: values.notes.trim() === "" ? null : values.notes.trim(),
            searches: values.searches.map((s) => ({ label: s.label.trim(), link: s.link.trim() })),
          }),
        )
      }
      submitLabel={t("auditReviews.form.continue")}
      secondary={{ label: t("dialog.cancel"), onClick: onClose }}
    >
      <Text size="sm">{t("auditReviews.form.intro")}</Text>
      <Group grow align="flex-start">
        <TextInput
          type="date"
          label={t("auditReviews.form.from")}
          max={last}
          data-autofocus
          {...form.getInputProps("period_from")}
        />
        <TextInput type="date" label={t("auditReviews.form.to")} max={last} {...form.getInputProps("period_to")} />
      </Group>
      <MultiSelect
        label={t("auditReviews.form.units")}
        description={t("auditReviews.form.unitsHint")}
        data={(units.data?.data ?? []).map((unit) => ({ value: unit.id, label: unit.name }))}
        searchable
        clearable
        nothingFoundMessage={t("auditReviews.form.nothingFound")}
        {...form.getInputProps("unit_ids")}
      />
      <MultiSelect
        label={t("auditReviews.form.eventTypes")}
        description={t("auditReviews.form.eventTypesHint")}
        data={(types.data?.data ?? []).map((type) => ({ value: type.code, label: type.name }))}
        searchable
        clearable
        nothingFoundMessage={t("auditReviews.form.nothingFound")}
        {...form.getInputProps("event_types")}
      />
      <Stack gap={4}>
        <Text size="sm" fw={500} id="review-outcome">
          {t("auditReviews.form.outcome")}
        </Text>
        <SegmentedControl
          aria-labelledby="review-outcome"
          data={auditReviewOutcomes.map((value) => ({ value, label: t(`auditReviews.outcomes.${value}`) }))}
          {...form.getInputProps("outcome")}
        />
      </Stack>
      <Textarea
        label={t("auditReviews.form.notes")}
        description={t("auditReviews.form.notesHint")}
        autosize
        minRows={2}
        maxRows={8}
        {...form.getInputProps("notes")}
      />
      <Stack gap={6}>
        <Text size="sm" fw={500}>
          {t("auditReviews.form.searches")}
        </Text>
        <Text size="xs" c="dimmed">
          {t("auditReviews.form.searchesHint")}
        </Text>
        {form.values.searches.map((_, index) => (
          <Group key={index} align="flex-start" wrap="nowrap" gap="xs">
            <TextInput
              label={t("auditReviews.form.searchLabel", { number: index + 1 })}
              style={{ flex: 1 }}
              {...form.getInputProps(`searches.${index}.label`)}
            />
            <TextInput
              label={t("auditReviews.form.searchAddress", { number: index + 1 })}
              placeholder="/audit?event_type=identity.login_failed"
              style={{ flex: 2 }}
              {...form.getInputProps(`searches.${index}.link`)}
            />
            <ActionIcon
              variant="subtle"
              mt={26}
              aria-label={t("auditReviews.form.removeSearch", { number: index + 1 })}
              onClick={() => form.removeListItem("searches", index)}
            >
              <IconTrash size={16} aria-hidden="true" />
            </ActionIcon>
          </Group>
        ))}
        {form.values.searches.length < searchesLimit && (
          <Button
            variant="subtle"
            size="compact-sm"
            w="fit-content"
            leftSection={<IconPlus size={14} aria-hidden="true" />}
            onClick={() => form.insertListItem("searches", { label: "", link: "" })}
          >
            {t("auditReviews.form.addSearch")}
          </Button>
        )}
      </Stack>
    </Form>
  );
}
