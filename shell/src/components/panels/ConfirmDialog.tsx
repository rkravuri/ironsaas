import { useTranslation } from "react-i18next";
import { Button, Group, Modal, Stack, Text } from "@mantine/core";

export interface ConfirmDialogProps {
  opened: boolean;
  title: string;
  /** What will happen, in words the user can act on. */
  message: string;
  confirmLabel: string;
  cancelLabel?: string;
  /** The action destroys something: the primary turns to danger, the words still say it. */
  danger?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

/** A question with one primary answer and a way out (brief section 8). */
export function ConfirmDialog({
  opened,
  title,
  message,
  confirmLabel,
  cancelLabel,
  danger = false,
  busy = false,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const { t } = useTranslation();
  return (
    <Modal
      opened={opened}
      onClose={onCancel}
      title={title}
      centered
      closeButtonProps={{ "aria-label": t("dialog.close") }}
    >
      <Stack>
        <Text>{message}</Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={onCancel} disabled={busy}>
            {cancelLabel ?? t("dialog.cancel")}
          </Button>
          <Button onClick={onConfirm} loading={busy} {...(danger ? { color: "danger" } : {})}>
            {confirmLabel}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
