import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { NavLink as RouterNavLink, Outlet, useLocation } from "react-router";
import { AppShell, Burger, Group, Menu, NavLink, ScrollArea, Text, Title, UnstyledButton } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconChevronDown, IconLogout, IconUser } from "@tabler/icons-react";
import { useSession } from "@/auth/session-context";
import { manifests } from "@/modules";
import { navigationFor } from "@/modules/registry";

/** Header, navigation from the manifests filtered by the session's permissions, the signed-in principal and sign out (S1 plan 3.10). */
export function Layout() {
  const { t } = useTranslation();
  const session = useSession();
  const navigation = useMemo(() => navigationFor(manifests, session.permissions, { strict: import.meta.env.DEV }), [session.permissions]);
  const location = useLocation();
  const [opened, { toggle, close }] = useDisclosure();

  return (
    <AppShell header={{ height: 56 }} navbar={{ width: 240, breakpoint: "sm", collapsed: { mobile: !opened } }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Group>
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" aria-label="Toggle navigation" />
            <Title order={4}>{t("app.name")}</Title>
            <Text c="dimmed" size="sm" visibleFrom="sm">
              {window.location.hostname}
            </Text>
          </Group>
          <Menu position="bottom-end" withinPortal>
            <Menu.Target>
              <UnstyledButton aria-label={t("app.signedInAs", { name: session.me?.display_name ?? "" })}>
                <Group gap={6}>
                  <IconUser size={18} />
                  <Text size="sm">{session.me?.display_name}</Text>
                  <IconChevronDown size={14} />
                </Group>
              </UnstyledButton>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>{session.me?.login_id}</Menu.Label>
              <Menu.Item component={RouterNavLink} to="/me" leftSection={<IconUser size={16} />}>
                {t("modules.identity.profile")}
              </Menu.Item>
              <Menu.Item leftSection={<IconLogout size={16} />} onClick={() => void session.signOut()}>
                {t("app.signOut")}
              </Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="xs">
        <ScrollArea type="auto">
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
        <Outlet />
      </AppShell.Main>
    </AppShell>
  );
}
