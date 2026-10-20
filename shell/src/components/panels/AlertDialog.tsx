import { useId, type ReactNode } from "react";
import { Box, FocusTrap, Group, Overlay, Paper, Portal, Stack, Text, Title, getDefaultZIndex } from "@mantine/core";
import { useFocusReturn } from "@mantine/hooks";

export interface AlertDialogProps {
  opened: boolean;
  title: string;
  message: ReactNode;
  /** The answers, the primary last with `data-autofocus`: the one that is safe to press at once. */
  actions: ReactNode;
  /** Anything under the message, such as why the last answer failed. */
  children?: ReactNode;
}

/**
 * A question that interrupts (WAI-ARIA alertdialog): it takes focus, announces its title and its message, keeps focus
 * inside until it is answered, and gives focus back where it was. Mantine's Modal is a plain dialog and cannot take
 * this role, so the frame is built from its parts; it has no close button and no Escape, because closing is an answer.
 */
export function AlertDialog({ opened, title, message, actions, children }: AlertDialogProps) {
  const id = useId();
  useFocusReturn({ opened, shouldReturnFocus: true });
  if (!opened) {
    return null;
  }
  const layer = getDefaultZIndex("modal");
  return (
    <Portal>
      <Overlay fixed zIndex={layer} backgroundOpacity={0.45} />
      <Box
        pos="fixed"
        inset={0}
        p="md"
        style={{ zIndex: layer + 1, display: "flex", alignItems: "center", justifyContent: "center" }}
      >
        <FocusTrap active>
          <Paper
            role="alertdialog"
            aria-modal="true"
            aria-labelledby={`${id}-title`}
            aria-describedby={`${id}-message`}
            data-panel="alert-dialog"
            withBorder
            shadow="xl"
            radius="md"
            p="lg"
            maw={460}
            w="100%"
          >
            <Stack gap="md">
              <Title order={2} size="h4" id={`${id}-title`}>
                {title}
              </Title>
              <Text id={`${id}-message`} size="sm">
                {message}
              </Text>
              {children}
              <Group justify="flex-end" gap="sm">
                {actions}
              </Group>
            </Stack>
          </Paper>
        </FocusTrap>
      </Box>
    </Portal>
  );
}
