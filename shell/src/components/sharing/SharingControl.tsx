import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Checkbox, Group, Paper, Radio, Stack, Text, VisuallyHidden } from "@mantine/core";
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
  // The record's own unit sees it whatever the mode, so it is not offered.
  const choices = sharing.choices.filter((choice) => choice.id !== sharing.owning_unit.id);

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
            choices={choices}
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
 * Several units chosen on the tree: a checkbox each, indented under its parent, its parent named for assistive
 * technology. A share lists each unit on its own, so choosing a unit never chooses the units below it.
 */
export function UnitChecklist({ label, choices, value, onChange, error }: { label: string; choices: readonly SharingChoice[]; value: string[]; onChange: (units: string[]) => void; error?: string | undefined }) {
  const { t } = useTranslation();
  const byId = useMemo(() => new Map(choices.map((choice) => [choice.id, choice])), [choices]);
  const top = choices.reduce((least, choice) => Math.min(least, choice.depth), Number.POSITIVE_INFINITY);
  return (
    <Checkbox.Group label={label} value={value} onChange={onChange} error={error}>
      <Paper withBorder p="xs" mt={6} mah={260} style={{ overflow: "auto" }}>
        <Stack gap={6}>
          {choices.map((choice) => {
            const parent = choice.parent_id !== null ? byId.get(choice.parent_id) : undefined;
            return (
              <Checkbox
                key={choice.id}
                value={choice.id}
                pl={(choice.depth - top) * 20}
                label={
                  <>
                    {choice.name}
                    {parent && <VisuallyHidden component="span">{t("sharing.under", { parent: parent.name })}</VisuallyHidden>}
                  </>
                }
              />
            );
          })}
        </Stack>
      </Paper>
    </Checkbox.Group>
  );
}
