import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Alert, Code, Loader, Stack, Table, Text, Title } from "@mantine/core";
import { ApiError } from "@/api/client";
import { fetchUsers } from "@/api/identity";

/**
 * The first protected page: the users of this tenant through the list envelope. T16 replaces the table with the
 * DataGrid and server paging, sorting and filtering; the data path stays.
 */
export function UsersPage() {
  const { t } = useTranslation();
  const query = useQuery({
    queryKey: ["users", "page-1"],
    queryFn: ({ signal }) => fetchUsers("page_size=50&sort=login_id", signal),
  });

  return (
    <Stack>
      <Title order={2}>{t("users.title")}</Title>
      {query.isPending && <Loader />}
      {query.error && (
        <Alert color="red" role="alert">
          {query.error.message}
          {query.error instanceof ApiError && query.error.correlationId && (
            <Text size="xs" c="dimmed" mt={4}>
              {t("errors.correlation", { id: "" })}
              <Code>{query.error.correlationId}</Code>
            </Text>
          )}
        </Alert>
      )}
      {query.data && query.data.data.length === 0 && <Text c="dimmed">{t("users.empty")}</Text>}
      {query.data && query.data.data.length > 0 && (
        <>
          <Table striped highlightOnHover withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("users.loginId")}</Table.Th>
                <Table.Th>{t("users.displayName")}</Table.Th>
                <Table.Th>{t("users.type")}</Table.Th>
                <Table.Th>{t("users.status")}</Table.Th>
                <Table.Th>{t("users.lastLogin")}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {query.data.data.map((user) => (
                <Table.Tr key={user.id}>
                  <Table.Td>{user.login_id}</Table.Td>
                  <Table.Td>{user.display_name}</Table.Td>
                  <Table.Td>{user.type}</Table.Td>
                  <Table.Td>{user.status}</Table.Td>
                  <Table.Td>{user.last_login_at ? new Date(user.last_login_at).toLocaleString() : t("users.never")}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
          <Text size="sm" c="dimmed">
            {t("users.count", { count: query.data.data.length, total: query.data.meta.total_count })}
          </Text>
        </>
      )}
    </Stack>
  );
}
