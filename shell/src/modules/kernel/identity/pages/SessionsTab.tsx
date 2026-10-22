import { useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Badge, Button, Group, Stack, Table, Text, Title, VisuallyHidden } from "@mantine/core";
import { IconLogout } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import {
  endMyOtherSessions,
  endMySession,
  fetchMySessions,
  sessionsQueryKey,
  type Me,
  type SessionRecord,
} from "@/api/identity";
import { useSession } from "@/auth/session-context";
import { useSetting } from "@/auth/settings";
import { useSessionWatch } from "@/auth/session-watch";
import { ConfirmDialog, EmptyState, LoadingState } from "@/components/panels";
import { deviceOf, placeOf } from "./session-words";

const when = (value: string | number, language: string) =>
  new Date(value).toLocaleString(language, { dateStyle: "medium", timeStyle: "short" });

/**
 * My profile's Sessions tab (TF-021, TF-022 AC4): this session's times, as the warning knows them, then every live session
 * of the person, with the browser, the place, when it started and was last used, and how it signed in; any other one
 * ends from here, or all of them at once ("Sign out everywhere else"); this one ends by signing out. An impersonator
 * sees the person's sessions but changes none: the API refuses, and the actions are not offered.
 */
export function SessionsTab({ me }: { me: Me }) {
  const { t, i18n } = useTranslation();
  const session = useSession();
  const queryClient = useQueryClient();
  const idleMinutes = useSetting("identity.session.idle_minutes");
  const absoluteHours = useSetting("identity.session.absolute_hours");
  const watch = useSessionWatch();
  const times = useSyncExternalStore(watch.subscribe, watch.getTimes);
  const mine = useQuery({ queryKey: [...sessionsQueryKey, "mine"], queryFn: ({ signal }) => fetchMySessions(signal) });
  const [ending, setEnding] = useState<SessionRecord | "others" | undefined>();
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<ApiError | undefined>();
  const [done, setDone] = useState<string | undefined>();
  const impersonated = me.impersonation != null;

  const end = async () => {
    if (!ending) {
      return;
    }
    setBusy(true);
    setProblem(undefined);
    try {
      if (ending === "others") {
        const result = await endMyOtherSessions();
        setDone(t("sessions.mine.endedOthers", { count: result.sessions_ended }));
      } else {
        await endMySession(ending.id);
        setDone(t("sessions.mine.ended", { device: deviceOf(ending, t) }));
      }
      await queryClient.invalidateQueries({ queryKey: sessionsQueryKey });
    } catch (error) {
      setProblem(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
      setEnding(undefined);
    }
  };

  const rows = mine.data?.data ?? [];
  const others = rows.filter((row) => !row.current);
  return (
    <Stack gap="xl" data-sessions-tab>
      <Stack gap="sm" align="flex-start">
        <Title order={3} size="h4">
          {t("sessions.mine.thisTitle")}
        </Title>
        <Table variant="vertical" withTableBorder maw={640}>
          <Table.Tbody>
            {[
              [
                t("profile.session.started"),
                me.session.started_at ? when(me.session.started_at, i18n.language) : t("profile.session.unknown"),
              ],
              [t("profile.session.idle"), when(times?.idleEndsAt ?? me.session.idle_expires_at, i18n.language)],
              [t("profile.session.absolute"), when(me.session.expires_at, i18n.language)],
            ].map(([label, value]) => (
              <Table.Tr key={label}>
                <Table.Th w={220}>{label}</Table.Th>
                <Table.Td>{value}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        <Text size="sm" c="dimmed" maw={640}>
          {t("profile.session.limits", { idle: idleMinutes, hours: absoluteHours })} {t("profile.session.explain")}
        </Text>
        <Button
          variant="default"
          leftSection={<IconLogout size={16} aria-hidden="true" />}
          onClick={() => void session.signOut()}
        >
          {t("app.signOut")}
        </Button>
      </Stack>

      <Stack gap="sm">
        <Group justify="space-between" align="flex-end" wrap="wrap">
          <Stack gap={2}>
            <Title order={3} size="h4">
              {t("sessions.mine.title")}
            </Title>
            <Text size="sm" c="dimmed" maw={640}>
              {t("sessions.mine.intro")}
            </Text>
          </Stack>
          {!impersonated && others.length > 0 && (
            <Button variant="default" onClick={() => setEnding("others")}>
              {t("sessions.mine.endOthers")}
            </Button>
          )}
        </Group>
        {done && (
          <Alert
            color="success"
            role="status"
            withCloseButton
            closeButtonLabel={t("dialog.close")}
            onClose={() => setDone(undefined)}
          >
            {done}
          </Alert>
        )}
        {problem && (
          <Alert color="danger" role="alert">
            {problem.message}
            {problem.correlationId !== undefined && (
              <Text size="xs" c="dimmed" mt={4}>
                {t("errors.correlation", { id: problem.correlationId })}
              </Text>
            )}
          </Alert>
        )}
        {mine.isPending ? (
          <LoadingState height={120} />
        ) : mine.isError ? (
          <Alert color="danger" role="alert" title={t("sessions.mine.failed")}>
            {mine.error instanceof ApiError ? mine.error.message : t("errors.unexpected")}
          </Alert>
        ) : rows.length === 0 ? (
          <EmptyState
            drawing="nothingYet"
            size="sm"
            title={t("sessions.empty.title")}
            text={t("sessions.empty.text")}
          />
        ) : (
          <Table withTableBorder striped>
            <Table.Caption>{t("sessions.mine.caption")}</Table.Caption>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("sessions.columns.device")}</Table.Th>
                <Table.Th>{t("sessions.columns.place")}</Table.Th>
                <Table.Th>{t("sessions.columns.started")}</Table.Th>
                <Table.Th>{t("sessions.columns.lastActivity")}</Table.Th>
                <Table.Th>{t("sessions.columns.method")}</Table.Th>
                <Table.Th>
                  <VisuallyHidden>{t("sessions.columns.actions")}</VisuallyHidden>
                </Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((row) => (
                <Table.Tr key={row.id} data-current={row.current ? "true" : undefined}>
                  <Table.Td>
                    {deviceOf(row, t)}{" "}
                    {row.current && (
                      <Badge variant="light" color="info" size="sm">
                        {t("sessions.mine.current")}
                      </Badge>
                    )}
                  </Table.Td>
                  <Table.Td>{placeOf(row, t)}</Table.Td>
                  <Table.Td>{when(row.started_at, i18n.language)}</Table.Td>
                  <Table.Td>{when(row.last_activity_at, i18n.language)}</Table.Td>
                  <Table.Td>{t(`sessions.methods.${row.method}`, { defaultValue: row.method })}</Table.Td>
                  <Table.Td>
                    {!row.current && !impersonated && (
                      <Button
                        variant="subtle"
                        size="compact-sm"
                        onClick={() => setEnding(row)}
                        aria-label={t("sessions.mine.endNamed", { device: deviceOf(row, t), place: placeOf(row, t) })}
                      >
                        {t("sessions.mine.end")}
                      </Button>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </Stack>
      <ConfirmDialog
        opened={ending !== undefined}
        title={ending === "others" ? t("sessions.mine.endOthersTitle") : t("sessions.mine.endTitle")}
        message={
          ending === "others"
            ? t("sessions.mine.endOthersMessage", { count: others.length })
            : ending
              ? t("sessions.mine.endMessage", { device: deviceOf(ending, t), place: placeOf(ending, t) })
              : ""
        }
        confirmLabel={ending === "others" ? t("sessions.mine.endOthers") : t("sessions.mine.end")}
        busy={busy}
        onConfirm={() => void end()}
        onCancel={() => setEnding(undefined)}
      />
    </Stack>
  );
}
