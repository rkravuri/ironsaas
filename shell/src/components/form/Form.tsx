import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, Group, Stack, Text } from "@mantine/core";
import type { UseFormReturnType } from "@mantine/form";
import { ApiError } from "@/api/client";
import { applyProblem, type ProblemSummary } from "./problems";

export interface FormProps<Values extends Record<string, unknown>> {
  form: UseFormReturnType<Values>;
  /** Saves. An ApiError's field problems land on their fields; the rest shows at the top with the correlation id. */
  onSubmit: (values: Values) => Promise<void>;
  submitLabel: string;
  /** The one other action, cancel or back; never a second primary (brief section 8). */
  secondary?: { label: string; onClick: () => void };
  children: ReactNode;
}

/** The form every page uses: labels above, one column, inline validation after the first blur, the primary action last. */
export function Form<Values extends Record<string, unknown>>({ form, onSubmit, submitLabel, secondary, children }: FormProps<Values>) {
  const { t } = useTranslation();
  const [summary, setSummary] = useState<ProblemSummary | undefined>();
  const [submitting, setSubmitting] = useState(false);

  const submit = form.onSubmit(async (values) => {
    setSubmitting(true);
    setSummary(undefined);
    try {
      await onSubmit(values);
    } catch (error) {
      if (error instanceof ApiError) {
        setSummary(applyProblem(form, error));
      } else {
        setSummary({ message: t("errors.unexpected"), correlationId: undefined, unmapped: [] });
      }
    } finally {
      setSubmitting(false);
    }
  });

  return (
    <form onSubmit={submit} noValidate>
      <Stack>
        {children}
        {summary && summary.message !== "" && (
          <Alert color="danger" role="alert">
            {summary.message}
            {summary.correlationId && (
              <Text size="xs" c="dimmed" mt={4}>
                {t("errors.correlation", { id: summary.correlationId })}
              </Text>
            )}
          </Alert>
        )}
        <Group justify="flex-end">
          {secondary && (
            <Button variant="default" onClick={secondary.onClick}>
              {secondary.label}
            </Button>
          )}
          <Button type="submit" loading={submitting}>
            {submitLabel}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}
