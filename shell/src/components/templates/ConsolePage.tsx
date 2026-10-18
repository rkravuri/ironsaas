import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Anchor, Box, Group, Paper, Stack, Text, Title } from "@mantine/core";
import { PageOpener } from "./PageOpener";

export interface ConsoleSection {
  id: string;
  title: string;
  description?: string;
  children: ReactNode;
}

export interface ConsolePageProps {
  title: string;
  description?: string;
  sections: ConsoleSection[];
}

/** A console page: settings-like sections with a description each, and a contents list on wide screens (TF-170). */
export function ConsolePage({ title, description, sections }: ConsolePageProps) {
  const { t } = useTranslation();
  return (
    <Stack gap="lg" data-template="console">
      <PageOpener title={title} {...(description !== undefined ? { description } : {})} />
      <Group align="flex-start" wrap="nowrap" gap="xl">
        <Box component="nav" aria-label={t("console.contents")} visibleFrom="md" w={200} style={{ flexShrink: 0 }}>
          <Stack gap="xs">
            {sections.map((section) => (
              <Anchor key={section.id} href={`#${section.id}`} size="sm">
                {section.title}
              </Anchor>
            ))}
          </Stack>
        </Box>
        <Stack gap="xl" style={{ flex: 1, minWidth: 0 }}>
          {sections.map((section) => (
            <section key={section.id} aria-labelledby={`${section.id}-title`}>
              <Stack gap="xs">
                <Title order={3} id={`${section.id}-title`}>
                  <span id={section.id}>{section.title}</span>
                </Title>
                {section.description !== undefined && (
                  <Text c="dimmed" size="sm">
                    {section.description}
                  </Text>
                )}
                <Paper withBorder p="md">
                  {section.children}
                </Paper>
              </Stack>
            </section>
          ))}
        </Stack>
      </Group>
    </Stack>
  );
}
