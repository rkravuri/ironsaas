import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Alert, Badge, Group, List, Select, Stack, Table, Text, Title } from "@mantine/core";
import { IconCircleCheck, IconCircleDashed } from "@tabler/icons-react";
import { z } from "zod";
import { ApiError } from "@/api/client";
import {
  fetchReadiness,
  mergePartCodes,
  mergeUnit,
  previewMerge,
  type Merge,
  type MergePart,
  type Unit,
} from "@/api/authorization";
import { Form, useZodForm } from "@/components/form";
import { FormDialog, LoadingState } from "@/components/panels";
import { mergeTargets, pathLabel } from "./unit-tree";

const unitsQueryKey = ["authz", "organisation-units"] as const;

/** The names in one phrase as the reader's language joins a list, ending with how many more when not all are shown. */
function namesOf(part: MergePart, language: string, more: (count: number) => string): string {
  const names = part.count > part.items.length ? [...part.items, more(part.count - part.items.length)] : part.items;
  return new Intl.ListFormat(language, { style: "long", type: "conjunction" }).format(names);
}

/**
 * What the modules need of a unit before people work in it (TF-223, ADR-0038): each item in words with done or to do,
 * and whether the unit is ready. A tenant whose modules ask nothing has a ready unit and says so.
 */
export function UnitReadiness({ unit }: { unit: Unit }) {
  const { t } = useTranslation();
  const readiness = useQuery({
    queryKey: [...unitsQueryKey, unit.id, "readiness"],
    queryFn: ({ signal }) => fetchReadiness(unit.id, signal),
  });
  if (readiness.isPending) {
    return <LoadingState height={72} />;
  }
  if (readiness.isError) {
    return (
      <Text size="sm" c="dimmed">
        {t("units.readiness.failed")}
      </Text>
    );
  }
  const { ready, items } = readiness.data;
  const left = items.filter((item) => !item.done).length;
  return (
    <Stack gap="xs" data-readiness={ready ? "ready" : "not-ready"}>
      <Group justify="space-between" gap="sm">
        <Title order={3} size="h5">
          {t("units.readiness.title")}
        </Title>
        <Badge
          variant="light"
          color={ready ? "success" : "warning"}
          leftSection={
            ready ? <IconCircleCheck size={14} aria-hidden="true" /> : <IconCircleDashed size={14} aria-hidden="true" />
          }
        >
          {ready ? t("units.readiness.ready") : t("units.readiness.notReady", { count: left })}
        </Badge>
      </Group>
      {items.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t("units.readiness.none")}
        </Text>
      ) : (
        <List spacing={4} size="sm" listStyleType="none" aria-label={t("units.readiness.title")}>
          {items.map((item) => (
            <List.Item
              key={`${item.module}:${item.code}`}
              icon={
                item.done ? (
                  <IconCircleCheck size={16} aria-hidden="true" />
                ) : (
                  <IconCircleDashed size={16} aria-hidden="true" />
                )
              }
            >
              <Text span size="sm">
                {t(item.done ? "units.readiness.done" : "units.readiness.toDo", { label: item.label })}
              </Text>
            </List.Item>
          ))}
        </List>
      )}
    </Stack>
  );
}

/**
 * Merge a unit into another (TF-223 AC1, ADR-0038): choose the unit that stays, read what moves, by kind, before
 * anything changes, then merge. The preview is the API's own, with its refusal in words when the merge is not possible
 * (the root, a unit below, an inactive unit, a kind the unit that stays cannot hold, a unit you may not manage).
 */
export function MergeUnitDialog({
  unit,
  units,
  onClose,
  onDone,
}: {
  unit: Unit;
  units: readonly Unit[];
  onClose: () => void;
  onDone: (merged: Merge) => Promise<void>;
}) {
  const { t } = useTranslation();
  const targets = useMemo(() => mergeTargets(units, unit), [units, unit]);
  const schema = useMemo(() => z.object({ intoId: z.string().min(1, t("units.merge.intoRequired")) }), [t]);
  const form = useZodForm({ schema, initialValues: { intoId: "" } });
  const into = form.getValues().intoId;
  const preview = useQuery({
    queryKey: [...unitsQueryKey, unit.id, "merge-preview", into],
    queryFn: ({ signal }) => previewMerge(unit.id, into, signal),
    enabled: into !== "",
    retry: false,
    // A preview is what the tree holds now: read again each time the dialog asks.
    staleTime: 0,
    gcTime: 0,
  });
  const intoName = units.find((candidate) => candidate.id === into)?.name;

  const submit = async (values: { intoId: string }) => {
    if (!preview.isSuccess) {
      form.setFieldError("intoId", preview.isError ? t("units.merge.chooseAnother") : t("units.merge.previewFirst"));
      return;
    }
    await onDone(await mergeUnit(unit.id, values.intoId));
  };

  return (
    <FormDialog opened size="lg" title={t("units.merge.title", { name: unit.name })} onClose={onClose}>
      <Form
        form={form}
        onSubmit={submit}
        submitLabel={
          intoName !== undefined
            ? t("units.merge.submit", { from: unit.name, into: intoName })
            : t("units.merge.submitPlain")
        }
        secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      >
        <Text size="sm">{t("units.merge.intro", { name: unit.name })}</Text>
        <Select
          label={t("units.merge.into")}
          description={t("units.merge.intoHint")}
          data={targets.map((target) => ({ value: target.id, label: pathLabel(units, target.id) }))}
          searchable
          nothingFoundMessage={t("units.merge.nothingFound")}
          data-autofocus
          {...form.getInputProps("intoId")}
        />
        {into !== "" && intoName !== undefined && (
          <div aria-live="polite">
            {preview.isPending ? (
              <LoadingState height={160} />
            ) : preview.isError ? (
              <Alert color="danger" role="alert" title={t("units.merge.refused")}>
                {preview.error instanceof ApiError ? preview.error.message : t("errors.unexpected")}
                {preview.error instanceof ApiError && preview.error.correlationId !== undefined && (
                  <Text size="xs" c="dimmed" mt={4}>
                    {t("errors.correlation", { id: preview.error.correlationId })}
                  </Text>
                )}
              </Alert>
            ) : (
              <MergePreview merge={preview.data} />
            )}
          </div>
        )}
      </Form>
    </FormDialog>
  );
}

/** What a merge moves, a row for each kind of thing, in words: how many, which, and what stays behind and why. */
export function MergePreview({ merge }: { merge: Merge }) {
  const { t, i18n } = useTranslation();
  const from = merge.from.name;
  const into = merge.into.name;
  const more = (count: number) => t("units.merge.more", { count });
  const words = (code: (typeof mergePartCodes)[number]): string[] => {
    const parts = merge.parts.filter((part) => part.code === code && part.count + part.conflicts.length > 0);
    if (parts.length === 0) {
      return [t("units.merge.nothing")];
    }
    return parts.flatMap((part) => {
      const moved =
        part.count === 0
          ? []
          : code === "records"
            ? [
                t("units.merge.parts.recordsOf", {
                  count: part.count,
                  type: part.record_type_name ?? part.record_type ?? "",
                  module: part.module,
                }),
              ]
            : code === "shares"
              ? [t("units.merge.parts.sharesMove", { count: part.count, from, into })]
              : [
                  t(`units.merge.parts.${code}Move`, {
                    count: part.count,
                    into,
                    names: namesOf(part, i18n.language, more),
                  }),
                ];
      const dropped =
        part.conflicts.length === 0
          ? []
          : [
              t(`units.merge.parts.${code}Dropped`, {
                count: part.conflicts.length,
                into,
                names: new Intl.ListFormat(i18n.language, { style: "long", type: "conjunction" }).format(
                  part.conflicts,
                ),
              }),
            ];
      return [...moved, ...dropped];
    });
  };

  return (
    <Stack gap="xs" data-merge-preview>
      <Title order={3} size="h5">
        {t("units.merge.previewTitle", { from, into })}
      </Title>
      <Table variant="vertical" withTableBorder layout="fixed">
        <Table.Tbody>
          {mergePartCodes.map((code) => (
            <Table.Tr key={code}>
              <Table.Th w={170} scope="row">
                {t(`units.merge.kinds.${code}`)}
              </Table.Th>
              <Table.Td>
                <Stack gap={2}>
                  {words(code).map((line) => (
                    <Text key={line} size="sm">
                      {line}
                    </Text>
                  ))}
                </Stack>
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      <Text size="sm">{t("units.merge.after", { from, into })}</Text>
    </Stack>
  );
}
