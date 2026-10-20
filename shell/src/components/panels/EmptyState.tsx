import type { ReactNode } from "react";
import { Stack, Text, Title } from "@mantine/core";
import { illustrations, type IllustrationName } from "@/design/illustrations";

/** One drawing per meaning (brief section 3). */
export type EmptyStateDrawing = Extract<IllustrationName, "nothingYet" | "nothingFound" | "noAccess" | "allDone">;

export interface EmptyStateProps {
  drawing: EmptyStateDrawing;
  title: string;
  text?: string;
  /** The one thing to do about it, if there is one. */
  action?: ReactNode;
  size?: "sm" | "md";
}

/** Nothing here, and what it means: the drawing is decoration beside the title that says it. */
export function EmptyState({ drawing, title, text, action, size = "md" }: EmptyStateProps) {
  const Illustration = illustrations[drawing];
  return (
    <Stack
      align="center"
      gap="xs"
      py={size === "md" ? "xl" : "md"}
      data-panel="empty-state"
      data-drawing-name={drawing}
    >
      <Illustration decorative size={size} />
      <Title order={3} size={size === "md" ? "h3" : "h5"}>
        {title}
      </Title>
      {text !== undefined && (
        <Text c="dimmed" size="sm" ta="center">
          {text}
        </Text>
      )}
      {action}
    </Stack>
  );
}
