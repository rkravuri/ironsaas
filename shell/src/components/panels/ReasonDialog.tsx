import { useMemo, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Text, Textarea } from "@mantine/core";
import { z } from "zod";
import { Form, useZodForm } from "@/components/form";
import { FormDialog } from "./FormDialog";

export interface ReasonDialogProps {
  opened: boolean;
  title: string;
  /** What will happen, in words the user can act on. */
  message: string;
  reasonLabel: string;
  /** Who reads the reason. */
  reasonHint: string;
  confirmLabel: string;
  /** More fields before the reason, such as a duration or a typed confirmation, inside the same form. */
  extra?: ReactNode;
  onSubmit: (reason: string) => Promise<void>;
  onClose: () => void;
}

interface ReasonValues extends Record<string, unknown> {
  reason: string;
}

/**
 * An action that needs a reason the audit trail keeps (ADR-0049): ending a session, revoking access, signing in as someone.
 * The reason is required; the API checks it again and answers why when it refuses, at the top of the form.
 */
export function ReasonDialog({
  opened,
  title,
  message,
  reasonLabel,
  reasonHint,
  confirmLabel,
  extra,
  onSubmit,
  onClose,
}: ReasonDialogProps) {
  const { t } = useTranslation();
  const schema = useMemo(() => z.object({ reason: z.string().trim().min(1, t("reason.required")).max(500) }), [t]);
  const form = useZodForm<ReasonValues>({ schema, initialValues: { reason: "" } });
  return (
    <FormDialog opened={opened} title={title} onClose={onClose}>
      <Form
        form={form}
        onSubmit={(values) => onSubmit(values.reason.trim())}
        submitLabel={confirmLabel}
        secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      >
        <Text size="sm">{message}</Text>
        {extra}
        <Textarea
          label={reasonLabel}
          description={reasonHint}
          required
          autosize
          minRows={2}
          maxLength={500}
          data-autofocus
          {...form.getInputProps("reason")}
        />
      </Form>
    </FormDialog>
  );
}
