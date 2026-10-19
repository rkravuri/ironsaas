import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Modal } from "@mantine/core";

export interface FormDialogProps {
  opened: boolean;
  title: string;
  onClose: () => void;
  /** A Form from components/form, with its own primary action. */
  children: ReactNode;
}

/** A form in a dialog: the form keeps its one primary action, the dialog only frames it. */
export function FormDialog({ opened, title, onClose, children }: FormDialogProps) {
  const { t } = useTranslation();
  return (
    <Modal opened={opened} onClose={onClose} title={title} centered closeButtonProps={{ "aria-label": t("dialog.close") }}>
      {children}
    </Modal>
  );
}
