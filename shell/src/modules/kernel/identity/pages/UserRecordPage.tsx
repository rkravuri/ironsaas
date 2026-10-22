import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Table, Text } from "@mantine/core";
import { ApiError } from "@/api/client";
import { fetchUser, type UserRecord } from "@/api/identity";
import { EmptyState, LoadingState } from "@/components/panels";
import { RecordPage } from "@/components/templates";
import { useRecentName } from "@/home/recent";
import { AccessTab } from "./AccessTab";
import { SignInTab } from "./SignInTab";
import { UserActions } from "./UserActions";
import { userStatus } from "./user-status";

const when = (value: string | null): string => (value ? new Date(value).toLocaleString() : "");

/**
 * One user on the record template (A2): the details and the Access tab, with the actions an administrator of the
 * person's unit may take (T2): unlock, a recovery link, deactivate and reactivate; and how a person signs in: their
 * company logins and the SSO-only mark (T4, ADR-0048). A user outside the reader's scope
 * answers 404 from the API, and the page says "not found" the same way as for an id that does not exist (ADR-0031).
 */
export function UserRecordPage() {
  const { t } = useTranslation();
  const { id = "" } = useParams();
  const user = useQuery({ queryKey: ["identity", "users", id], queryFn: ({ signal }) => fetchUser(id, signal) });

  if (user.isPending) {
    return <LoadingState />;
  }
  if (user.isError) {
    if (user.error instanceof ApiError && user.error.status === 404) {
      return (
        <EmptyState
          drawing="nothingFound"
          title={t("users.record.notFoundTitle")}
          text={t("users.record.notFoundText")}
          action={
            <Button component={Link} to="/users" variant="default">
              {t("users.record.backToList")}
            </Button>
          }
        />
      );
    }
    return (
      <Alert color="danger" role="alert" title={t("users.record.failed")}>
        {user.error instanceof ApiError ? user.error.message : t("errors.unexpected")}
        {user.error instanceof ApiError && user.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: user.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }
  return <UserRecordView user={user.data} />;
}

function UserRecordView({ user }: { user: UserRecord }) {
  const { t } = useTranslation();
  useRecentName(user.display_name);
  const [now] = useState(() => Date.now());
  const locked = user.locked_until != null && Date.parse(user.locked_until) > now;
  const rows: [string, string][] = [
    ...(locked ? [[t("users.lockedUntil"), when(user.locked_until ?? null)] as [string, string]] : []),
    [t("users.email"), user.email ?? ""],
    [t("users.homeUnit"), user.home_unit?.name ?? t("users.record.noHomeUnit")],
    [t("users.lastLogin"), when(user.last_login_at) || t("users.never")],
    [t("users.created"), when(user.created_at)],
    [t("users.updated"), when(user.updated_at)],
  ];
  const details = (
    <Table variant="vertical" withTableBorder>
      <Table.Tbody>
        {rows.map(([label, value]) => (
          <Table.Tr key={label}>
            <Table.Th w={180}>{label}</Table.Th>
            <Table.Td>{value}</Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
  return (
    <RecordPage
      header={{
        title: user.display_name,
        code: user.login_id,
        status: userStatus(user.status, t),
        meta: [
          { label: t("users.type"), value: t(`users.types.${user.type}`, { defaultValue: user.type }) },
          ...(user.home_unit ? [{ label: t("users.homeUnit"), value: user.home_unit.name }] : []),
          ...(locked ? [{ label: t("users.login"), value: t("users.loginLocked") }] : []),
        ],
        actions: <UserActions user={user} />,
      }}
      tabs={[
        { value: "details", label: t("users.record.details"), panel: details },
        { value: "access", label: t("users.record.access"), panel: <AccessTab user={user} /> },
        ...(user.type === "user"
          ? [{ value: "sign-in", label: t("users.record.signIn"), panel: <SignInTab user={user} /> }]
          : []),
      ]}
    />
  );
}
