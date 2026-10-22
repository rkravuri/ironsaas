import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Group, Stack, Table, Text, Title } from "@mantine/core";
import { ApiError } from "@/api/client";
import { setSsoOnly, type UserRecord } from "@/api/identity";
import { useAccessAt } from "@/auth/access";
import { EmptyState } from "@/components/panels";

const manage = "identity.users.manage";

const when = (value: string): string => new Date(value).toLocaleString();

/**
 * How a person signs in (TF-014, ADR-0048): the company logins they are linked to, and whether they sign in only through
 * one. An administrator who may manage the person changes the mark; a provider that is the only way in for everyone it
 * links holds them without the mark, and the tab says so.
 */
export function SignInTab({ user }: { user: UserRecord }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const access = useAccessAt(user.home_unit ? { unit: user.home_unit.id } : undefined, [manage]);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();
  const links = user.sso_links ?? [];
  const marked = user.sso_only === true;
  const byProvider = user.sso_only_by_provider === true;
  const canManage = !access.pending && access.can(manage);

  const change = async (ssoOnly: boolean) => {
    setBusy(true);
    setFailure(undefined);
    try {
      const updated = await setSsoOnly(user.id, ssoOnly);
      queryClient.setQueryData(["identity", "users", user.id], updated);
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Stack gap="lg" data-sign-in-tab>
      <Stack gap="xs">
        <Title order={3} size="h5">
          {t("users.signIn.how")}
        </Title>
        <Text size="sm" data-sso-only={marked || byProvider ? "yes" : "no"}>
          {marked
            ? t("users.signIn.ssoOnlyMarked")
            : byProvider
              ? t("users.signIn.ssoOnlyByProvider")
              : t("users.signIn.passwordAllowed")}
        </Text>
        {failure && (
          <Alert color="danger" role="alert">
            {failure.message}
            {failure.correlationId !== undefined && (
              <Text size="xs" c="dimmed" mt={4}>
                {t("errors.correlation", { id: failure.correlationId })}
              </Text>
            )}
          </Alert>
        )}
        {canManage && user.type === "user" && (
          <Group>
            <Button variant="default" loading={busy} onClick={() => void change(!marked)}>
              {marked ? t("users.signIn.allowPassword") : t("users.signIn.requireCompanyLogin")}
            </Button>
          </Group>
        )}
      </Stack>
      <Stack gap="xs">
        <Title order={3} size="h5">
          {t("users.signIn.links")}
        </Title>
        {links.length === 0 ? (
          <EmptyState drawing="nothingYet" size="sm" title={t("users.signIn.noLinks")} />
        ) : (
          <Table withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("users.signIn.provider")}</Table.Th>
                <Table.Th>{t("users.signIn.email")}</Table.Th>
                <Table.Th>{t("users.signIn.linked")}</Table.Th>
                <Table.Th>{t("users.signIn.lastSignIn")}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {links.map((link) => (
                <Table.Tr key={link.provider_id}>
                  <Table.Td>{link.provider_name}</Table.Td>
                  <Table.Td>{link.email}</Table.Td>
                  <Table.Td>{when(link.linked_at)}</Table.Td>
                  <Table.Td>{when(link.last_sign_in_at)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </Stack>
    </Stack>
  );
}
