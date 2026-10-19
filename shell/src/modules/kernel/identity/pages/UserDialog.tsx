import { useTranslation } from "react-i18next";
import { Modal, Stack, Table } from "@mantine/core";
import type { User } from "@/api/identity";
import { RecordHeader } from "@/components/panels";
import { userStatus } from "./user-status";

const when = (value: string | null): string => (value ? new Date(value).toLocaleString() : "");

/** A user's details on the record header; the API has no read by id yet, so the row's own data is shown. */
export function UserDialog({ user, onClose }: { user: User | undefined; onClose: () => void }) {
  const { t } = useTranslation();
  const rows: [string, string][] = user
    ? [
        [t("users.email"), user.email ?? ""],
        [t("users.lastLogin"), when(user.last_login_at) || t("users.never")],
        [t("users.created"), when(user.created_at)],
        [t("users.updated"), when(user.updated_at)],
      ]
    : [];
  return (
    <Modal opened={user !== undefined} onClose={onClose} title={t("users.details")} size="lg" centered>
      {user && (
        <Stack gap="md">
          <RecordHeader title={user.display_name} code={user.login_id} status={userStatus(user.status, t)} meta={[{ label: t("users.type"), value: user.type }]} />
          <Table variant="vertical">
            <Table.Tbody>
              {rows.map(([label, value]) => (
                <Table.Tr key={label}>
                  <Table.Th w={180}>{label}</Table.Th>
                  <Table.Td>{value}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Stack>
      )}
    </Modal>
  );
}
