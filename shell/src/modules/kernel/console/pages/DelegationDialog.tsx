import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Checkbox, Group, Stack, TextInput, Textarea } from "@mantine/core";
import { z } from "zod";
import { createDelegation, fetchDuties, type Delegation } from "@/api/authorization";
import { useSession } from "@/auth/session-context";
import { Form, useZodForm } from "@/components/form";
import { FormDialog, LoadingState } from "@/components/panels";
import { periodOf, today } from "./delegation-words";
import { PersonPicker } from "./PersonPicker";

export interface DelegationDialogProps {
  opened: boolean;
  /** An administrator delegates for someone else (authz.delegations.manage at their home unit); otherwise your own. */
  forSomeone: boolean;
  onClose: () => void;
  onDone: (delegation: Delegation) => Promise<void>;
}

const reasonMax = 500;

/**
 * Delegating duties (TF-037, ADR-0037): to whom, which duties in words, from which day to which, and why. The API
 * refuses what may not be (to oneself, an overlap, a circle, onward), each with a sentence that lands on its field or
 * at the top of the form.
 */
export function DelegationDialog({ opened, forSomeone, onClose, onDone }: DelegationDialogProps) {
  const { t } = useTranslation();
  return (
    <FormDialog opened={opened} title={t(forSomeone ? "delegations.create.titleForSomeone" : "delegations.create.title")} onClose={onClose}>
      {opened && <DelegationForm forSomeone={forSomeone} onClose={onClose} onDone={onDone} />}
    </FormDialog>
  );
}

function DelegationForm({ forSomeone, onClose, onDone }: Omit<DelegationDialogProps, "opened">) {
  const { t } = useTranslation();
  const { me } = useSession();
  const duties = useQuery({ queryKey: ["authz", "duties"], queryFn: ({ signal }) => fetchDuties(signal), staleTime: 5 * 60_000 });
  const first = today();
  const schema = useMemo(
    () =>
      z
        .object({
          delegator_id: forSomeone ? z.string().min(1, t("delegations.form.delegatorRequired")) : z.string(),
          delegate_id: z.string().min(1, t("delegations.form.delegateRequired")),
          duties: z.array(z.string()).min(1, t("delegations.form.dutiesRequired")),
          from: z.string().min(1, t("delegations.form.fromRequired")),
          until: z.string().min(1, t("delegations.form.untilRequired")),
          reason: z
            .string()
            .trim()
            .min(1, t("delegations.form.reasonRequired"))
            .max(reasonMax, t("delegations.form.reasonTooLong", { max: reasonMax })),
        })
        .refine((values) => values.until >= values.from, { path: ["until"], message: t("delegations.form.untilBeforeFrom") })
        .refine((values) => values.until >= first, { path: ["until"], message: t("delegations.form.untilPassed") }),
    [forSomeone, first, t],
  );
  const form = useZodForm({ schema, initialValues: { delegator_id: "", delegate_id: "", duties: [] as string[], from: first, until: first, reason: "" } });
  const delegator = forSomeone ? form.values.delegator_id : me?.id;

  const submit = async (values: z.infer<typeof schema>) => {
    const period = periodOf(values.from, values.until);
    await onDone(
      await createDelegation({
        ...(forSomeone ? { delegator_id: values.delegator_id } : {}),
        delegate_id: values.delegate_id,
        duties: values.duties,
        reason: values.reason.trim(),
        ...period,
      }),
    );
  };

  return (
    <Form form={form} onSubmit={submit} submitLabel={t("delegations.create.submit")} secondary={{ label: t("dialog.cancel"), onClick: onClose }}>
      {forSomeone && <PersonPicker label={t("delegations.form.delegator")} description={t("delegations.form.delegatorHint")} data-autofocus {...form.getInputProps("delegator_id")} />}
      <PersonPicker
        label={t("delegations.form.delegate")}
        description={t("delegations.form.delegateHint")}
        exclude={delegator ? [delegator] : []}
        {...(forSomeone ? {} : { "data-autofocus": true })}
        {...form.getInputProps("delegate_id")}
      />
      {duties.isPending ? (
        <LoadingState height={80} />
      ) : (
        <Checkbox.Group label={t("delegations.form.duties")} description={t("delegations.form.dutiesHint")} {...form.getInputProps("duties")}>
          <Stack gap="xs" mt="xs">
            {(duties.data?.data ?? []).map((duty) => (
              <Checkbox key={duty.code} value={duty.code} label={duty.name} description={duty.description} />
            ))}
          </Stack>
        </Checkbox.Group>
      )}
      <Group grow align="flex-start">
        <TextInput type="date" label={t("delegations.form.from")} min={first} {...form.getInputProps("from")} />
        <TextInput type="date" label={t("delegations.form.until")} min={form.values.from || first} {...form.getInputProps("until")} />
      </Group>
      <Textarea label={t("delegations.form.reason")} description={t("delegations.form.reasonHint")} autosize minRows={2} maxRows={6} {...form.getInputProps("reason")} />
    </Form>
  );
}
