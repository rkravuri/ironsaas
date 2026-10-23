import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Modal } from "@mantine/core";
import { IconHistory } from "@tabler/icons-react";
import { RecordHistory } from "./RecordHistory";

/**
 * A record's History where the page has no record template (TF-062, ADR-0051): a button that opens the record's events,
 * each save as its fields, old and new, in a dialog. A page shows it to a person who may read the audit trail; with no
 * record chosen it is there but off.
 */
export function RecordHistoryButton({
  record,
  name,
  compact = false,
}: {
  record: { recordType: string; recordId: string } | undefined;
  /** The record as the reader knows it, for the button's accessible name and the dialog's title. */
  name: string;
  /** A row's own button, small and quiet, rather than the page's. */
  compact?: boolean;
}) {
  const { t } = useTranslation();
  const [opened, setOpened] = useState(false);
  return (
    <>
      <Button
        variant={compact ? "subtle" : "default"}
        {...(compact ? { size: "compact-sm" } : {})}
        leftSection={<IconHistory size={compact ? 14 : 16} aria-hidden="true" />}
        disabled={record === undefined}
        onClick={() => setOpened(true)}
        {...(record !== undefined ? { "aria-label": t("history.openNamed", { name }) } : {})}
      >
        {t("history.open")}
      </Button>
      <Modal
        opened={opened && record !== undefined}
        onClose={() => setOpened(false)}
        title={t("history.of", { name })}
        centered
        size="xl"
        closeButtonProps={{ "aria-label": t("dialog.close") }}
      >
        {record !== undefined && <RecordHistory recordType={record.recordType} recordId={record.recordId} />}
      </Modal>
    </>
  );
}
