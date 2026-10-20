import { useMemo, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Box, Button, Checkbox, Group, Paper, Radio, Stack, Text } from "@mantine/core";
import { IconBuildingCommunity, IconLock, IconShare } from "@tabler/icons-react";
import { z } from "zod";
import { changeSharing, fetchSharing, type RecordSharing, type SharingChoice, type SharingMode } from "@/api/authorization";
import { Form, useZodForm } from "@/components/form";
import { FormDialog, LoadingState } from "@/components/panels";

const modeIcons = { private: IconLock, shared: IconShare, tenant: IconBuildingCommunity } as const;

/**
 * Who sees a business record beyond its unit (TF-222, ADR-0038), for any record page: give it the record type and id.
 * It reads the sharing through the API and changes it there; the record's own policy decides who may.
 */
export function SharingControl({ recordType, recordId }: { recordType: string; recordId: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const key = useMemo(() => ["authz", "record-sharing", recordType, recordId] as const, [recordType, recordId]);
  const sharing = useQuery({ queryKey: key, queryFn: ({ signal }) => fetchSharing(recordType, recordId, signal) });
  if (sharing.isPending) {
    return <LoadingState height={64} />;
  }
  if (sharing.isError) {
    return (
      <Text size="sm" c="dimmed">
        {t("sharing.failed")}
      </Text>
    );
  }
  return (
    <SharingPanel
      sharing={sharing.data}
      onChange={async (mode, units) => {
        queryClient.setQueryData(key, await changeSharing(recordType, recordId, { mode, units }));
      }}
    />
  );
}

/** The sharing in words with its mark, and for a reader who may change it, the button to do so. The gallery shows it with sample data. */
export function SharingPanel({ sharing, onChange }: { sharing: RecordSharing; onChange: (mode: SharingMode, units: string[]) => Promise<void> }) {
  const { t, i18n } = useTranslation();
  const [changing, setChanging] = useState(false);
  const Icon = modeIcons[sharing.mode];
  const unit = sharing.owning_unit.name;
  const listed = new Intl.ListFormat(i18n.language, { style: "long", type: "conjunction" }).format(sharing.units.map((shared) => shared.name));
  const words = sharing.mode === "private" ? t("sharing.words.private", { unit }) : sharing.mode === "shared" ? t("sharing.words.shared", { unit, units: listed }) : t("sharing.words.tenant", { unit });

  return (
    <Stack gap={6} data-sharing={sharing.mode}>
      <Text size="sm" fw={500}>
        {t("sharing.label")}
      </Text>
      <Group gap="xs" wrap="nowrap" align="flex-start">
        <Icon size={18} aria-hidden="true" style={{ flexShrink: 0, marginTop: 1 }} />
        <Text size="sm">{words}</Text>
      </Group>
      {sharing.may_change && (
        <Group>
          <Button variant="default" size="xs" onClick={() => setChanging(true)}>
            {t("sharing.change")}
          </Button>
        </Group>
      )}
      {changing && (
        <SharingDialog
          sharing={sharing}
          onClose={() => setChanging(false)}
          onSave={async (mode, units) => {
            await onChange(mode, units);
            setChanging(false);
          }}
        />
      )}
    </Stack>
  );
}

interface SharingValues extends Record<string, unknown> {
  mode: SharingMode;
  units: string[];
}

/** The three modes in words, and for "shared" the units to list, on the tree. */
function SharingDialog({ sharing, onClose, onSave }: { sharing: RecordSharing; onClose: () => void; onSave: (mode: SharingMode, units: string[]) => Promise<void> }) {
  const { t } = useTranslation();
  const unit = sharing.owning_unit.name;
  const schema = useMemo(
    () =>
      z
        .object({ mode: z.enum(["private", "shared", "tenant"]), units: z.array(z.string()) })
        .refine((values) => values.mode !== "shared" || values.units.length > 0, { path: ["units"], message: t("sharing.unitsRequired") }),
    [t],
  );
  const form = useZodForm<SharingValues>({ schema, initialValues: { mode: sharing.mode, units: sharing.units.map((shared) => shared.id) } });
  const { mode, units } = form.getValues();

  return (
    <FormDialog opened title={t("sharing.title")} onClose={onClose}>
      <Form form={form} onSubmit={(values) => onSave(values.mode, values.mode === "shared" ? values.units : [])} submitLabel={t("sharing.save")} secondary={{ label: t("dialog.cancel"), onClick: onClose }}>
        <Radio.Group label={t("sharing.mode")} {...form.getInputProps("mode")}>
          <Stack gap="sm" mt={6}>
            <Radio value="private" label={t("sharing.modes.private")} description={t("sharing.modes.privateHint", { unit })} data-autofocus={mode === "private" ? true : undefined} />
            <Radio value="shared" label={t("sharing.modes.shared")} description={t("sharing.modes.sharedHint", { unit })} data-autofocus={mode === "shared" ? true : undefined} />
            <Radio value="tenant" label={t("sharing.modes.tenant")} description={t("sharing.modes.tenantHint")} data-autofocus={mode === "tenant" ? true : undefined} />
          </Stack>
        </Radio.Group>
        {mode === "shared" && (
          <UnitChecklist
            label={t("sharing.units")}
            choices={sharing.choices}
            ownUnitId={sharing.owning_unit.id}
            value={units}
            onChange={(next) => form.setFieldValue("units", next)}
            error={typeof form.errors.units === "string" ? form.errors.units : undefined}
          />
        )}
      </Form>
    </FormDialog>
  );
}

/**
 * Several units chosen on the tree (ADR-0038): a checkbox each, in lists nested as the tree is, so assistive technology
 * announces each unit's level and the units it sits in. A share lists each unit on its own, so choosing a unit never
 * chooses the units below it. The record's own unit sees it whatever the mode: it stays in the tree, in words, without
 * a checkbox, so the units below it keep their place.
 */
export function UnitChecklist({
  label,
  choices,
  ownUnitId,
  value,
  onChange,
  error,
}: {
  label: string;
  choices: readonly SharingChoice[];
  ownUnitId: string;
  value: string[];
  onChange: (units: string[]) => void;
  error?: string | undefined;
}) {
  const { t } = useTranslation();
  const children = useMemo(() => {
    const present = new Set(choices.map((choice) => choice.id));
    const below = new Map<string | null, SharingChoice[]>();
    for (const choice of choices) {
      // A unit whose parent is not offered starts a branch of its own; the API sends tree order, kept here.
      const parent = choice.parent_id !== null && present.has(choice.parent_id) ? choice.parent_id : null;
      below.set(parent, [...(below.get(parent) ?? []), choice]);
    }
    return below;
  }, [choices]);

  const branch = (parent: string | null, level: number): ReactNode => {
    const units = children.get(parent) ?? [];
    return units.length === 0 ? null : (
      <Box component="ul" m={0} p={0} pl={level === 0 ? 0 : 24} style={{ listStyle: "none" }}>
        {units.map((unit) => (
          <Box component="li" key={unit.id} mt={6}>
            {unit.id === ownUnitId ? (
              <Text size="sm" c="dimmed" data-own-unit>
                {t("sharing.ownUnit", { unit: unit.name })}
              </Text>
            ) : (
              <Checkbox value={unit.id} label={unit.name} />
            )}
            {branch(unit.id, level + 1)}
          </Box>
        ))}
      </Box>
    );
  };

  return (
    <Checkbox.Group label={label} value={value} onChange={onChange} error={error}>
      <Paper withBorder p="xs" pt={0} mt={6} mah={260} style={{ overflow: "auto" }}>
        {branch(null, 0)}
      </Paper>
    </Checkbox.Group>
  );
}
