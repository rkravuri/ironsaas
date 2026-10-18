import type { ReactNode } from "react";
import { Group, Stack, Text, Title } from "@mantine/core";

export interface PageOpenerProps {
  title: string;
  /** One line under the title; the rest of the page is the work. */
  description?: string;
  /** An optional spot drawing beside the title (brief section 3). */
  drawing?: ReactNode;
  actions?: ReactNode;
}

/** The moment of a page (brief section 2): the title in the display family, one line, a drawing if there is one, the actions. */
export function PageOpener({ title, description, drawing, actions }: PageOpenerProps) {
  return (
    <Group justify="space-between" align="flex-start" wrap="nowrap" gap="md">
      <Group align="flex-start" gap="md" wrap="nowrap">
        {drawing}
        <Stack gap={4}>
          <Title order={2} variant="display">
            {title}
          </Title>
          {description !== undefined && (
            <Text c="dimmed" size="sm">
              {description}
            </Text>
          )}
        </Stack>
      </Group>
      {actions !== undefined && (
        <Group gap="sm" wrap="nowrap">
          {actions}
        </Group>
      )}
    </Group>
  );
}
