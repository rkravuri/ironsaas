import { useTranslation } from "react-i18next";
import { Badge, Group, Stack, Table, Text, Title } from "@mantine/core";
import { useSession } from "@/auth/session-context";

/** What /me says about the signed-in principal: handy for support and for seeing one's own permissions. */
export function ProfilePage() {
  const { t } = useTranslation();
  const { me } = useSession();
  if (!me) {
    return null;
  }
  return (
    <Stack>
      <Title order={2}>{t("profile.title")}</Title>
      <Table variant="vertical" withTableBorder>
        <Table.Tbody>
          <Table.Tr>
            <Table.Th>{t("profile.loginId")}</Table.Th>
            <Table.Td>{me.login_id}</Table.Td>
          </Table.Tr>
          <Table.Tr>
            <Table.Th>{t("profile.displayName")}</Table.Th>
            <Table.Td>{me.display_name}</Table.Td>
          </Table.Tr>
          <Table.Tr>
            <Table.Th>{t("profile.type")}</Table.Th>
            <Table.Td>{me.type}</Table.Td>
          </Table.Tr>
          <Table.Tr>
            <Table.Th>{t("profile.permissions")}</Table.Th>
            <Table.Td>
              {me.permissions.length === 0 ? (
                <Text c="dimmed">{t("profile.none")}</Text>
              ) : (
                <Group gap="xs">
                  {me.permissions.map((permission) => (
                    <Badge key={permission} variant="light">
                      {permission}
                    </Badge>
                  ))}
                </Group>
              )}
            </Table.Td>
          </Table.Tr>
        </Table.Tbody>
      </Table>
      <Text size="sm" c="dimmed">
        {t("profile.sessionEnds", { when: new Date(me.session.expires_at).toLocaleString() })}
      </Text>
    </Stack>
  );
}
