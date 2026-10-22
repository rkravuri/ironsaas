import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { NavLink as RouterNavLink, Outlet, useLocation } from "react-router";
import { AppShell, Burger, Group, NavLink, ScrollArea, Text, Title } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconHome } from "@tabler/icons-react";
import { usePreferenceSync } from "@/auth/preferences";
import { SessionWarning } from "@/auth/SessionWarning";
import { useSession } from "@/auth/session-context";
import { TenantMark } from "@/design/patterns";
import { useRememberPages } from "./remember-pages";
import { manifests } from "@/modules";
import { navigationFor } from "@/modules/registry";
import { useActingChoice } from "./acting";
import { ActingBand, ActingForControl } from "./ActingFor";
import { HelpButton } from "./HelpPanel";
import { KillSwitchBanner } from "./KillSwitchBanner";
import { ReauthenticationDialog } from "@/auth/Reauthentication";
import { MfaEnrolBanner } from "./MfaEnrolBanner";
import { PasswordExpiryBanner } from "./PasswordExpiryBanner";
import { ProfileMenu } from "./ProfileMenu";
import { Search, SearchButton } from "./Search";
import { useWorkingUnitChoice } from "./working-unit";
import { WorkingUnitControl, WorkingUnitFailure } from "./WorkingUnit";

/**
 * The frame of every signed-in page (S1 plan 3.10, ADR-0033): the header with the tenant's mark, search, help and the
 * profile menu, and nothing that does not work yet; the navigation from the manifests with Home first; the session
 * warning; for administrators, the kill-switch banner above the page (ADR-0035); and the person's colour scheme and
 * density kept with them across browsers (ADR-0036). The "Acting for" control joins the header, and its band the top of
 * the page, only while the person holds a delegation (ADR-0037). The choice of the unit the person works for joins it
 * only when there is more than one unit to choose (ADR-0038). A notifications bell joins when S4 gives it something to do.
 */
export function Layout() {
  const { t } = useTranslation();
  const session = useSession();
  const navigation = useMemo(
    () => navigationFor(manifests, session.permissions, { strict: import.meta.env.DEV }, session.entitlements.entitled),
    [session.permissions, session.entitlements],
  );
  const location = useLocation();
  const [opened, { toggle, close }] = useDisclosure();
  const acting = useActingChoice();
  const working = useWorkingUnitChoice();
  useRememberPages();
  usePreferenceSync();

  return (
    <AppShell
      header={{ height: 56 }}
      navbar={{ width: 240, breakpoint: "sm", collapsed: { mobile: !opened } }}
      padding="md"
    >
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Group wrap="nowrap" gap="sm">
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" aria-label={t("app.toggleNavigation")} />
            <TenantMark hostname={window.location.hostname} size={24} />
            <Title order={4}>{t("app.name")}</Title>
            <Text c="dimmed" size="sm" visibleFrom="md">
              {window.location.hostname}
            </Text>
          </Group>
          <Group wrap="nowrap" gap="sm">
            <WorkingUnitControl choice={working} />
            <ActingForControl choice={acting} />
            <SearchButton />
            <HelpButton />
            <ProfileMenu />
          </Group>
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="xs" aria-label={t("app.navigation")}>
        <ScrollArea type="auto">
          <NavLink
            component={RouterNavLink}
            to="/"
            end
            label={t("home.title")}
            leftSection={<IconHome size={16} />}
            active={location.pathname === "/"}
            onClick={close}
          />
          {navigation.map((group) => (
            <NavLink key={group.module} label={t(group.titleKey)} defaultOpened childrenOffset={12}>
              {group.entries.map((entry) => (
                <NavLink
                  key={entry.path}
                  component={RouterNavLink}
                  to={entry.path}
                  label={t(entry.labelKey)}
                  leftSection={entry.icon}
                  active={location.pathname === entry.path || location.pathname.startsWith(entry.path + "/")}
                  onClick={close}
                />
              ))}
            </NavLink>
          ))}
        </ScrollArea>
      </AppShell.Navbar>

      <AppShell.Main>
        <WorkingUnitFailure choice={working} />
        <ActingBand choice={acting} />
        <KillSwitchBanner />
        <PasswordExpiryBanner />
        <MfaEnrolBanner />
        <Outlet />
        <ReauthenticationDialog />
      </AppShell.Main>
      <Search />
      <SessionWarning />
    </AppShell>
  );
}
