import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Select, TextInput } from "@mantine/core";
import { z } from "zod";
import { createUnit, moveUnit, renameUnit, type Kinds, type Unit } from "@/api/authorization";
import { Form, useZodForm } from "@/components/form";
import { FormDialog } from "@/components/panels";
import { childKinds, moveTargets, pathLabel } from "./unit-tree";

const codePattern = /^[a-z0-9][a-z0-9-]*$/;

interface DialogProps {
  unit: Unit;
  onClose: () => void;
  onDone: (unit: Unit) => Promise<void>;
}

/** A unit under the selected one: its name, its code and a kind the parent's kind may hold (the provider decides, ADR-0029). */
export function CreateUnitDialog({ unit: parent, kinds, onClose, onDone }: DialogProps & { kinds: Kinds | undefined }) {
  const { t } = useTranslation();
  const allowed = childKinds(kinds, parent.kind);
  const schema = useMemo(
    () =>
      z.object({
        name: z.string().trim().min(1, t("units.form.nameRequired")).max(200, t("units.form.nameTooLong")),
        code: z.string().trim().min(1, t("units.form.codeRequired")).max(50, t("units.form.codeTooLong")).regex(codePattern, t("units.form.codeShape")),
        kind: z.string().min(1, t("units.form.kindRequired")),
      }),
    [t],
  );
  const form = useZodForm({ schema, initialValues: { name: "", code: "", kind: allowed[0]?.code ?? "" } });
  return (
    <FormDialog opened title={t("units.create.title", { parent: parent.name })} onClose={onClose}>
      <Form form={form} onSubmit={async (values) => onDone(await createUnit(parent.id, values))} submitLabel={t("units.create.submit")} secondary={{ label: t("dialog.cancel"), onClick: onClose }}>
        <TextInput label={t("units.name")} data-autofocus {...form.getInputProps("name")} />
        <TextInput label={t("units.code")} description={t("units.form.codeHint")} {...form.getInputProps("code")} />
        <Select label={t("units.kind")} data={allowed.map((kind) => ({ value: kind.code, label: kind.name }))} allowDeselect={false} {...form.getInputProps("kind")} />
      </Form>
    </FormDialog>
  );
}

export function RenameUnitDialog({ unit, onClose, onDone }: DialogProps) {
  const { t } = useTranslation();
  const schema = useMemo(() => z.object({ name: z.string().trim().min(1, t("units.form.nameRequired")).max(200, t("units.form.nameTooLong")) }), [t]);
  const form = useZodForm({ schema, initialValues: { name: unit.name } });
  return (
    <FormDialog opened title={t("units.rename.title", { name: unit.name })} onClose={onClose}>
      <Form form={form} onSubmit={async (values) => onDone(await renameUnit(unit.id, values.name))} submitLabel={t("units.rename.submit")} secondary={{ label: t("dialog.cancel"), onClick: onClose }}>
        <TextInput label={t("units.name")} data-autofocus {...form.getInputProps("name")} />
      </Form>
    </FormDialog>
  );
}

/** A new parent among the units the API would accept: active, outside the unit's own subtree, of a kind that may hold it. */
export function MoveUnitDialog({ unit, units, kinds, onClose, onDone }: DialogProps & { units: readonly Unit[]; kinds: Kinds | undefined }) {
  const { t } = useTranslation();
  const targets = moveTargets(units, unit, kinds);
  const schema = useMemo(() => z.object({ parentId: z.string().min(1, t("units.form.parentRequired")) }), [t]);
  const form = useZodForm({ schema, initialValues: { parentId: "" } });
  return (
    <FormDialog opened title={t("units.move.title", { name: unit.name })} onClose={onClose}>
      <Form form={form} onSubmit={async (values) => onDone(await moveUnit(unit.id, values.parentId))} submitLabel={t("units.move.submit")} secondary={{ label: t("dialog.cancel"), onClick: onClose }}>
        <Select
          label={t("units.move.parent")}
          description={t("units.move.hint")}
          data={targets.map((target) => ({ value: target.id, label: pathLabel(units, target.id) }))}
          searchable
          nothingFoundMessage={t("units.move.nothingFound")}
          data-autofocus
          {...form.getInputProps("parentId")}
        />
      </Form>
    </FormDialog>
  );
}
