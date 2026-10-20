import { useSyncExternalStore } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, SegmentedControl, Stack, Table, Text, useMantineColorScheme } from "@mantine/core";
import { IconLogout } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { fetchMyAssignments, type Assignment } from "@/api/authorization";
import type { Me } from "@/api/identity";
import { useSession } from "@/auth/session-context";
import { useSetting } from "@/auth/settings";
import { useSessionWatch } from "@/auth/session-watch";
import { EmptyState, LoadingState } from "@/components/panels";
import { RecordPage } from "@/components/templates";
import { densities, setDensity, useDensity, type Density } from "@/design/density";
import { schemeChoices, type SchemeChoice } from "@/design/scheme";
import { unitPathLabel } from "@/modules/kernel/console/pages/assignment-scope";
import { PersonalDelegations } from "@/modules/kernel/console/pages/PersonalDelegations";
import { userStatus } from "./user-status";

const when = (value: string | number) => new Date(value).toLocaleString();
const isScheme = (value: string): value is SchemeChoice => (schemeChoices as readonly string[]).includes(value);
const isDensity = (value: string): value is Density => (densities as readonly string[]).includes(value);

/**
 * My profile (D4) on the record template: the person's name, login identifier, home unit and status, then their
 * details, their own access in words, their preferences (A6), and their session. A signed-in principal is active by construction: the API ends
 * the sessions of anyone who is not (SessionService), so the status needs no request of its own.
 */
export function ProfilePage() {
  const { t } = useTranslation();
  const { me } = useSession();
  if (!me) {
    return null;
  }
  return (
    <RecordPage
      header={{
        title: me.display_name,
        code: me.login_id,
        status: userStatus("active", t),
        meta: [
          { label: t("users.homeUnit"), value: me.home_unit?.name ?? t("profile.noHomeUnit") },
          { label: t("users.type"), value: t(`users.types.${me.type}`, { defaultValue: me.type }) },
        ],
      }}
      tabs={[
        { value: "details", label: t("profile.tabs.details"), panel: <Details me={me} /> },
        { value: "access", label: t("profile.tabs.access"), panel: <MyAccess /> },
        { value: "delegations", label: t("profile.tabs.delegations"), panel: <PersonalDelegations /> },
        { value: "preferences", label: t("profile.tabs.preferences"), panel: <MyPreferences /> },
        { value: "session", label: t("profile.tabs.session"), panel: <MySession me={me} /> },
      ]}
    />
  );
}

function Facts({ rows }: { rows: [string, string][] }) {
  return (
    <Table variant="vertical" withTableBorder>
      <Table.Tbody>
        {rows.map(([label, value]) => (
          <Table.Tr key={label}>
            <Table.Th w={220}>{label}</Table.Th>
            <Table.Td>{value}</Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}

function Details({ me }: { me: Me }) {
  const { t } = useTranslation();
  return (
    <Facts
      rows={[
        [t("profile.displayName"), me.display_name],
        [t("profile.loginId"), me.login_id],
        [t("profile.type"), t(`users.types.${me.type}`, { defaultValue: me.type })],
        [t("profile.homeUnit"), me.home_unit?.name ?? t("profile.noHomeUnit")],
      ]}
    />
  );
}

/** The person's own assignments, read-only, each a sentence: the role, then where it applies (TF-033, ADR-0031). */
function MyAccess() {
  const { t } = useTranslation();
  const mine = useQuery({ queryKey: ["authz", "role-assignments", "mine"], queryFn: ({ signal }) => fetchMyAssignments(signal) });
  if (mine.isPending) {
    return <LoadingState height={120} />;
  }
  if (mine.isError) {
    return (
      <Alert color="danger" role="alert" title={t("profile.access.failed")}>
        {mine.error instanceof ApiError ? mine.error.message : t("errors.unexpected")}
        {mine.error instanceof ApiError && mine.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: mine.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }
  if (mine.data.data.length === 0) {
    return <EmptyState drawing="nothingYet" size="sm" title={t("profile.access.none")} text={t("profile.access.noneText")} />;
  }
  return (
    <Stack gap="sm">
      <Text size="sm" c="dimmed" maw={640}>
        {t("profile.access.intro")}
      </Text>
      <Stack gap="xs" component="ul" m={0} pl={0} style={{ listStyle: "none" }} aria-label={t("profile.access.label")}>
        {mine.data.data.map((assignment) => (
          <li key={assignment.id}>
            <Text size="sm">
              <Text span fw={600}>
                {assignment.role_name}
              </Text>
              {", "}
              {where(assignment, t)}
            </Text>
            <Text size="xs" c="dimmed">
              {t("profile.access.since", { date: new Date(assignment.created_at).toLocaleDateString() })}
            </Text>
          </li>
        ))}
      </Stack>
    </Stack>
  );
}

const where = (assignment: Assignment, t: TFunction): string =>
  t(`profile.access.scopes.${assignment.scope_kind}`, { unit: unitPathLabel(assignment) });

/**
 * The person's preferences (ADR-0036): the colour scheme and the density, saved with the person, so they follow them to
 * every browser they sign in on. The choice applies at once; the layout's sync saves it.
 */
function MyPreferences() {
  const { t } = useTranslation();
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const density = useDensity();
  return (
    <Stack gap="lg" align="flex-start">
      <Text size="sm" c="dimmed" maw={640}>
        {t("profile.preferences.intro")}
      </Text>
      <Stack gap={6}>
        <Text size="sm" fw={600}>
          {t("header.scheme.label")}
        </Text>
        <SegmentedControl
          aria-label={t("header.scheme.label")}
          value={colorScheme}
          onChange={(value) => {
            if (isScheme(value)) {
              setColorScheme(value);
            }
          }}
          data={schemeChoices.map((value) => ({ value, label: t(`header.scheme.${value}`) }))}
        />
      </Stack>
      <Stack gap={6}>
        <Text size="sm" fw={600}>
          {t("density.label")}
        </Text>
        <SegmentedControl
          aria-label={t("density.label")}
          value={density}
          onChange={(value) => {
            if (isDensity(value)) {
              setDensity(value);
            }
          }}
          data={densities.map((value) => ({ value, label: t(`density.${value}`) }))}
        />
        <Text size="xs" c="dimmed" maw={640}>
          {t("profile.preferences.densityExplain")}
        </Text>
      </Stack>
    </Stack>
  );
}

/**
 * The session (TF-021): when it started, when it ends without activity (as this tab knows it, moved by every answer)
 * and its absolute limit. The API has no "sign out everywhere" yet (TF-022), so the one action is Sign out, which ends
 * the session in every tab of this browser. The tenant's limits for new sessions are its settings (ADR-0036).
 */
function MySession({ me }: { me: Me }) {
  const { t } = useTranslation();
  const session = useSession();
  const idleMinutes = useSetting("identity.session.idle_minutes");
  const absoluteHours = useSetting("identity.session.absolute_hours");
  const watch = useSessionWatch();
  const times = useSyncExternalStore(watch.subscribe, watch.getTimes);
  return (
    <Stack gap="md" align="flex-start">
      <Facts
        rows={[
          [t("profile.session.started"), me.session.started_at ? when(me.session.started_at) : t("profile.session.unknown")],
          [t("profile.session.idle"), when(times?.idleEndsAt ?? me.session.idle_expires_at)],
          [t("profile.session.absolute"), when(me.session.expires_at)],
        ]}
      />
      <Text size="sm" c="dimmed" maw={640}>
        {t("profile.session.limits", { idle: idleMinutes, hours: absoluteHours })} {t("profile.session.explain")}
      </Text>
      <Button variant="default" leftSection={<IconLogout size={16} aria-hidden="true" />} onClick={() => void session.signOut()}>
        {t("app.signOut")}
      </Button>
    </Stack>
  );
}
