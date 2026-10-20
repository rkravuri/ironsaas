import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Group, Radio, Select, Stack, Text, TextInput, Textarea } from "@mantine/core";
import { z } from "zod";
import { createConflictRule, fetchPermissions, fetchRoles, updateConflictRule, type ConflictMode, type ConflictRule, type ConflictSideKind } from "@/api/authorization";
import { Form, useZodForm } from "@/components/form";
import { FormDialog } from "@/components/panels";
import { PersonPicker } from "./PersonPicker";

interface RuleValues extends Record<string, unknown> {
  name: string;
  firstKind: ConflictSideKind;
  firstValue: string;
  secondKind: ConflictSideKind;
  secondValue: string;
  mode: ConflictMode;
  reason: string;
  ownerId: string;
}

interface ConflictRuleDialogProps {
  opened: boolean;
  /** The rule to change; none for a new one. */
  rule?: ConflictRule | undefined;
  onClose: () => void;
  onDone: () => Promise<void>;
}

/**
 * A conflict rule, new or changed (TF-036, ADR-0039): its name, the two sides, each a permission or a role chosen by
 * its words, whether it blocks or warns, why, and who answers for it. The API checks the sides again; its field
 * problems land on their fields. The frame stays mounted so closing it gives the focus back to the button that opened
 * it; the form starts afresh each time it opens.
 */
export function ConflictRuleDialog({ opened, rule, onClose, onDone }: ConflictRuleDialogProps) {
  const { t } = useTranslation();
  return (
    <FormDialog opened={opened} size="lg" title={rule ? t("conflicts.editor.editTitle", { name: rule.name }) : t("conflicts.editor.createTitle")} onClose={onClose}>
      {opened && <ConflictRuleForm rule={rule} onClose={onClose} onDone={onDone} />}
    </FormDialog>
  );
}

function ConflictRuleForm({ rule, onClose, onDone }: Omit<ConflictRuleDialogProps, "opened">) {
  const { t } = useTranslation();
  const permissions = useQuery({
    queryKey: ["authz", "permissions"],
    queryFn: ({ signal }) => fetchPermissions(signal),
  });
  const roles = useQuery({
    queryKey: ["authz", "roles", "all"],
    queryFn: ({ signal }) => fetchRoles("page_size=200&sort=name", signal),
  });
  const permissionOptions = useMemo(
    () =>
      (permissions.data?.data ?? []).map((p) => ({
        value: p.name,
        label: `${p.description} (${p.name})`,
      })),
    [permissions.data],
  );
  const roleOptions = useMemo(() => (roles.data?.data ?? []).map((r) => ({ value: r.id, label: r.name })), [roles.data]);
  const schema = useMemo(
    () =>
      z.object({
        name: z.string().trim().min(1, t("conflicts.editor.nameRequired")).max(200, t("conflicts.editor.nameTooLong")),
        firstKind: z.enum(["permission", "role"]),
        firstValue: z.string().min(1, t("conflicts.editor.sideRequired")),
        secondKind: z.enum(["permission", "role"]),
        secondValue: z.string().min(1, t("conflicts.editor.sideRequired")),
        mode: z.enum(["block", "warn"]),
        reason: z.string().max(1000, t("conflicts.editor.reasonTooLong")),
        ownerId: z.string(),
      }),
    [t],
  );
  const form = useZodForm<RuleValues>({
    schema,
    initialValues: {
      name: rule?.name ?? "",
      firstKind: rule?.first.kind ?? "permission",
      firstValue: rule?.first.value ?? "",
      secondKind: rule?.second.kind ?? "permission",
      secondValue: rule?.second.value ?? "",
      mode: rule?.mode ?? "block",
      reason: rule?.reason ?? "",
      ownerId: rule?.owner_id ?? "",
    },
  });
  const values = form.getValues();

  const submit = async (chosen: RuleValues) => {
    const body = {
      name: chosen.name.trim(),
      first: { kind: chosen.firstKind, value: chosen.firstValue },
      second: { kind: chosen.secondKind, value: chosen.secondValue },
      mode: chosen.mode,
      reason: chosen.reason.trim(),
      ...(chosen.ownerId !== "" ? { owner_id: chosen.ownerId } : {}),
    };
    await (rule ? updateConflictRule(rule.id, body) : createConflictRule(body));
    await onDone();
  };

  const side = (prefix: "first" | "second", legend: string) => {
    const kind = values[`${prefix}Kind`];
    return (
      <Stack gap={6}>
        <Radio.Group
          label={legend}
          {...form.getInputProps(`${prefix}Kind`)}
          onChange={(next) => {
            form.setFieldValue(`${prefix}Kind`, next as ConflictSideKind);
            form.setFieldValue(`${prefix}Value`, "");
          }}
        >
          <Group mt={4}>
            <Radio value="permission" label={t("conflicts.editor.kinds.permission")} />
            <Radio value="role" label={t("conflicts.editor.kinds.role")} />
          </Group>
        </Radio.Group>
        <Select
          label={kind === "permission" ? t("conflicts.editor.permission") : t("conflicts.editor.role")}
          data={kind === "permission" ? permissionOptions : roleOptions}
          searchable
          nothingFoundMessage={t("conflicts.editor.nothingFound")}
          {...form.getInputProps(`${prefix}Value`)}
        />
      </Stack>
    );
  };

  return (
    <Form form={form} onSubmit={submit} submitLabel={rule ? t("conflicts.editor.submitEdit") : t("conflicts.editor.submitCreate")} secondary={{ label: t("dialog.cancel"), onClick: onClose }}>
      <TextInput label={t("conflicts.editor.name")} data-autofocus {...form.getInputProps("name")} />
      {side("first", t("conflicts.editor.first"))}
      {side("second", t("conflicts.editor.second"))}
      <Radio.Group label={t("conflicts.editor.mode")} {...form.getInputProps("mode")}>
        <Stack gap="xs" mt={6}>
          <Radio value="block" label={t("conflicts.modes.block")} description={t("conflicts.modeHints.block")} />
          <Radio value="warn" label={t("conflicts.modes.warn")} description={t("conflicts.modeHints.warn")} />
        </Stack>
      </Radio.Group>
      <Textarea label={t("conflicts.editor.reason")} description={t("conflicts.editor.reasonHint")} autosize minRows={2} {...form.getInputProps("reason")} />
      <PersonPicker
        label={t("conflicts.editor.owner")}
        description={t("conflicts.editor.ownerHint")}
        value={values.ownerId === "" ? null : values.ownerId}
        onChange={(value) => form.setFieldValue("ownerId", value ?? "")}
        initial={
          rule && rule.owner_name
            ? {
                id: rule.owner_id,
                display_name: rule.owner_name,
                home_unit: null,
              }
            : undefined
        }
      />
      {permissions.isError && (
        <Text size="sm" c="dimmed">
          {t("conflicts.editor.noCatalogue")}
        </Text>
      )}
    </Form>
  );
}
