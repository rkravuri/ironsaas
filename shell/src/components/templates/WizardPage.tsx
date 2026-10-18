import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Box, Button, Flex, Group, Paper, Stack, Stepper } from "@mantine/core";
import { PageOpener } from "./PageOpener";

export interface WizardStep {
  label: string;
  description?: string;
}

export interface WizardPageProps {
  title: string;
  description?: string;
  steps: WizardStep[];
  /** The index of the step being filled in. */
  active: number;
  onBack: () => void;
  /** Next, or finish on the last step. */
  onNext: () => void;
  busy?: boolean;
  /** What has been entered so far, beside the step. */
  summary?: ReactNode;
  /** The step's fields. */
  children: ReactNode;
}

/** A wizard: numbered steps, the current step's fields, the summary beside them, back and next (TF-170). */
export function WizardPage({ title, description, steps, active, onBack, onNext, busy = false, summary, children }: WizardPageProps) {
  const { t } = useTranslation();
  const last = active >= steps.length - 1;
  return (
    <Stack gap="lg" data-template="wizard">
      <PageOpener title={title} {...(description !== undefined ? { description } : {})} />
      <Stepper active={active} size="sm">
        {steps.map((step) => (
          <Stepper.Step key={step.label} label={step.label} {...(step.description !== undefined ? { description: step.description } : {})} />
        ))}
      </Stepper>
      <Flex gap="lg" direction={{ base: "column", md: "row" }} align="flex-start">
        <Box style={{ flex: 1, minWidth: 0 }}>
          <Stack gap="md">
            {children}
            <Group justify="flex-end">
              <Button variant="default" onClick={onBack} disabled={active === 0 || busy}>
                {t("wizard.back")}
              </Button>
              <Button onClick={onNext} loading={busy}>
                {last ? t("wizard.finish") : t("wizard.next")}
              </Button>
            </Group>
          </Stack>
        </Box>
        {summary !== undefined && (
          <Box w={{ base: "100%", md: 280 }}>
            <Paper withBorder p="md" component="aside" aria-label={t("wizard.summary")}>
              {summary}
            </Paper>
          </Box>
        )}
      </Flex>
    </Stack>
  );
}
