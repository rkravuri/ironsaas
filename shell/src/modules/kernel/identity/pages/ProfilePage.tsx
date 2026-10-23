import { useState } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, SegmentedControl, Select, Stack, Table, Text, useMantineColorScheme } from "@mantine/core";
import { ApiError } from "@/api/client";
import { fetchMyAssignments, type Assignment } from "@/api/authorization";
import type { Me } from "@/api/identity";
import { saveSetting } from "@/api/settings";
import { contextQueryKey } from "@/auth/entitlements";
import { useSession } from "@/auth/session-context";
import { useSetting } from "@/auth/settings";
import { EmptyState, LoadingState } from "@/components/panels";
import { RecordPage } from "@/components/templates";
import { densities, setDensity, useDensity, type Density } from "@/design/density";
import { schemeChoices, type SchemeChoice } from "@/design/scheme";
import { unitPathLabel } from "@/modules/kernel/console/pages/assignment-scope";
import { PersonalDelegations } from "@/modules/kernel/console/pages/PersonalDelegations";
import { SecurityTab } from "./SecurityTab";
import { SessionsTab } from "./SessionsTab";
import { userStatus } from "./user-status";
import { browserTimeZone, formatDate, formatTime, timeZones } from "@/i18n/dates";

const isScheme = (value: string): value is SchemeChoice => (schemeChoices as readonly string[]).includes(value);
const isDensity = (value: string): value is Density => (densities as readonly string[]).includes(value);

/**
 * My profile (D4) on the record template: the person's name, login identifier, home unit and status, then their
 * details, their own access in words, their preferences (A6), their password and security activity (T2), and their
 * sessions: this one and the others, which they end from here (T5, TF-022). A signed-in principal is active by construction: the API ends
 * the sessions of anyone who is not (SessionService), so the status needs no request of its own.
 */
export function ProfilePage() {
  const { t } = useTranslation();
  const { me } = useSession();
  // ?tab=security opens the Security tab: the grace banner and a sign-in by recovery code link to it (ADR-0047).
  const [params] = useSearchParams();
  const tab = params.get("tab");
  if (!me) {
    return null;
  }
  return (
    <RecordPage
      {...(tab ? { defaultTab: tab } : {})}
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
        { value: "security", label: t("profile.tabs.security"), panel: <SecurityTab /> },
        { value: "sessions", label: t("profile.tabs.sessions"), panel: <SessionsTab me={me} /> },
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
  const mine = useQuery({
    queryKey: ["authz", "role-assignments", "mine"],
    queryFn: ({ signal }) => fetchMyAssignments(signal),
  });
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
    return (
      <EmptyState drawing="nothingYet" size="sm" title={t("profile.access.none")} text={t("profile.access.noneText")} />
    );
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
              {t("profile.access.since", { date: formatDate(assignment.created_at) })}
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
      <TimeZoneChoice />
    </Stack>
  );
}

/**
 * The person's time zone (TF-065 AC2): every time the shell shows is in it, with its name. None chosen follows this
 * browser. Saved with the person at once, so it follows them to every browser, as the scheme does.
 */
function TimeZoneChoice() {
  const { t } = useTranslation();
  const zone = useSetting("shell.time_zone");
  const queryClient = useQueryClient();
  const [now] = useState(() => Date.now());
  const [saving, setSaving] = useState(false);
  const [failed, setFailed] = useState<string | undefined>();
  const data = [
    { value: "", label: t("profile.preferences.timeZoneBrowser", { zone: browserTimeZone() }) },
    ...timeZones().map((value) => ({ value, label: value })),
  ];
  return (
    <Stack gap={6} w="100%" maw={420}>
      <Select
        label={t("profile.preferences.timeZone")}
        description={t("profile.preferences.timeZoneExplain", { example: formatTime(now) })}
        searchable
        allowDeselect={false}
        data={data}
        value={zone}
        disabled={saving}
        onChange={(value) => {
          setSaving(true);
          setFailed(undefined);
          void saveSetting("shell.time_zone", { scope: "user" }, value ?? "")
            .then(() => queryClient.invalidateQueries({ queryKey: contextQueryKey }))
            .catch((error: unknown) => setFailed(error instanceof ApiError ? error.message : t("errors.unexpected")))
            .finally(() => setSaving(false));
        }}
      />
      {failed && (
        <Text size="sm" c="var(--mantine-color-error)" role="alert">
          {failed}
        </Text>
      )}
    </Stack>
  );
}
