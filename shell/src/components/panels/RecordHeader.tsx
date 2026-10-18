import type { ReactNode } from "react";
import { Badge, Group, Stack, Text, Title } from "@mantine/core";

/** A meaning, never a hue: the tone picks the theme colour, and the label says it in words (brief section 5). */
export type StatusTone = "neutral" | "info" | "success" | "warning" | "danger";

export interface RecordStatus {
  label: string;
  tone: StatusTone;
}

export interface RecordMeta {
  label: string;
  value: string;
}

export interface RecordHeaderProps {
  title: string;
  /** The record's code or identifier, in the monospace face. */
  code?: string;
  status?: RecordStatus;
  meta?: RecordMeta[];
  actions?: ReactNode;
  drawing?: ReactNode;
}

/** The head of a record (TF-171): the title, its status as a word, the facts that identify it, and its actions. */
export function RecordHeader({ title, code, status, meta, actions, drawing }: RecordHeaderProps) {
  return (
    <Group justify="space-between" align="flex-start" wrap="nowrap" gap="md" data-panel="record-header">
      <Group align="flex-start" gap="md" wrap="nowrap">
        {drawing}
        <Stack gap={4}>
          <Group gap="sm" align="center" wrap="wrap">
            <Title order={2} variant="display">
              {title}
            </Title>
            {status !== undefined && (
              <Badge variant={status.tone === "neutral" ? "default" : "light"} {...(status.tone !== "neutral" ? { color: status.tone } : {})} size="lg" radius="sm">
                {status.label}
              </Badge>
            )}
          </Group>
          {code !== undefined && (
            <Text c="dimmed" size="sm" ff="monospace">
              {code}
            </Text>
          )}
          {meta !== undefined && meta.length > 0 && (
            <Group gap="md">
              {meta.map((item) => (
                <Text key={item.label} size="sm">
                  <Text span c="dimmed">
                    {item.label}:{" "}
                  </Text>
                  {item.value}
                </Text>
              ))}
            </Group>
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
