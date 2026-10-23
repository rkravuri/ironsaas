import { useTranslation } from "react-i18next";
import { ActionIcon, Button, FileButton, Group, Stack, Table, Title, UnstyledButton } from "@mantine/core";
import { IconTrash, IconUpload } from "@tabler/icons-react";
import { EmptyState } from "./EmptyState";
import { formatDateTime } from "@/i18n/dates";

export interface Attachment {
  id: string;
  name: string;
  /** Bytes. */
  size: number;
  uploadedAt: string;
  uploadedBy: string;
}

export interface AttachmentPanelProps {
  attachments: Attachment[];
  /** The page stores the file through its module's API (object storage paths, ADR-0016); absent means read-only. */
  onUpload?: (file: File) => Promise<void>;
  onDownload?: (attachment: Attachment) => void;
  onDelete?: (attachment: Attachment) => Promise<void>;
}

const kilobytes = (size: number): number => Math.max(1, Math.round(size / 1024));

/** The files of a record (TF-171): the list, a download per file, an upload, a removal where the page allows it. */
export function AttachmentPanel({ attachments, onUpload, onDownload, onDelete }: AttachmentPanelProps) {
  const { t } = useTranslation();
  return (
    <Stack gap="sm" data-panel="attachments">
      <Group justify="space-between">
        <Title order={4}>{t("attachments.title")}</Title>
        <FileButton
          onChange={(file) => {
            if (file && onUpload) {
              void onUpload(file);
            }
          }}
        >
          {(props) => (
            <Button
              {...props}
              variant="default"
              size="xs"
              leftSection={<IconUpload size={16} />}
              disabled={onUpload === undefined}
            >
              {t("attachments.upload")}
            </Button>
          )}
        </FileButton>
      </Group>
      {attachments.length === 0 ? (
        <EmptyState drawing="nothingYet" title={t("attachments.empty")} size="sm" />
      ) : (
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t("attachments.name")}</Table.Th>
              <Table.Th>{t("attachments.size")}</Table.Th>
              <Table.Th>{t("attachments.uploaded")}</Table.Th>
              <Table.Th>{t("attachments.by")}</Table.Th>
              {onDelete !== undefined && <Table.Th />}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {attachments.map((attachment) => (
              <Table.Tr key={attachment.id}>
                <Table.Td>
                  {onDownload ? (
                    <UnstyledButton
                      onClick={() => onDownload(attachment)}
                      aria-label={t("attachments.download", { name: attachment.name })}
                      td="underline"
                    >
                      {attachment.name}
                    </UnstyledButton>
                  ) : (
                    attachment.name
                  )}
                </Table.Td>
                <Table.Td>{t("attachments.kilobytes", { count: kilobytes(attachment.size) })}</Table.Td>
                <Table.Td>{formatDateTime(attachment.uploadedAt)}</Table.Td>
                <Table.Td>{attachment.uploadedBy}</Table.Td>
                {onDelete !== undefined && (
                  <Table.Td>
                    <ActionIcon
                      variant="subtle"
                      aria-label={t("attachments.remove", { name: attachment.name })}
                      onClick={() => void onDelete(attachment)}
                    >
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Table.Td>
                )}
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Stack>
  );
}
