import { useTranslation } from "react-i18next";
import { NavLink as RouterNavLink } from "react-router";
import { Group, Menu, Text, UnstyledButton, VisuallyHidden, useMantineColorScheme } from "@mantine/core";
import {
  IconCheck,
  IconChevronDown,
  IconDeviceDesktop,
  IconLogout,
  IconMoon,
  IconSun,
  IconUser,
} from "@tabler/icons-react";
import { useSession } from "@/auth/session-context";
import { schemeChoices, type SchemeChoice } from "@/design/scheme";

const schemeIcons: Record<SchemeChoice, typeof IconSun> = { light: IconSun, dark: IconMoon, auto: IconDeviceDesktop };

/**
 * The profile menu (TF-170, ADR-0033): who is signed in and their home unit, My profile, the colour scheme (Light,
 * Dark, or the device's; kept per viewer under the key `public/scheme.js` reads) and sign out. The current scheme is
 * marked with a tick and said in words.
 */
export function ProfileMenu() {
  const { t } = useTranslation();
  const session = useSession();
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const me = session.me;
  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <UnstyledButton aria-label={t("app.signedInAs", { name: me?.display_name ?? "" })}>
          <Group gap={6} wrap="nowrap">
            <IconUser size={18} aria-hidden="true" />
            <Text size="sm" visibleFrom="sm">
              {me?.display_name}
            </Text>
            <IconChevronDown size={14} aria-hidden="true" />
          </Group>
        </UnstyledButton>
      </Menu.Target>
      <Menu.Dropdown miw={240}>
        <Menu.Label>{me?.login_id}</Menu.Label>
        <Text px="sm" pb={6} size="sm" data-home-unit>
          {me?.home_unit ? t("header.homeUnit", { unit: me.home_unit.name }) : t("header.noHomeUnit")}
        </Text>
        <Menu.Item component={RouterNavLink} to="/me" leftSection={<IconUser size={16} aria-hidden="true" />}>
          {t("modules.identity.profile")}
        </Menu.Item>
        <Menu.Divider />
        <Menu.Label>{t("header.scheme.label")}</Menu.Label>
        {schemeChoices.map((choice) => {
          const Icon = schemeIcons[choice];
          const current = colorScheme === choice;
          return (
            <Menu.Item
              key={choice}
              closeMenuOnClick={false}
              onClick={() => setColorScheme(choice)}
              leftSection={<Icon size={16} aria-hidden="true" />}
              rightSection={current ? <IconCheck size={16} aria-hidden="true" /> : undefined}
              data-scheme={choice}
              data-current={current ? "true" : undefined}
            >
              {t(`header.scheme.${choice}`)}
              {current && <VisuallyHidden>{t("header.scheme.current")}</VisuallyHidden>}
            </Menu.Item>
          );
        })}
        <Menu.Divider />
        <Menu.Item leftSection={<IconLogout size={16} aria-hidden="true" />} onClick={() => void session.signOut()}>
          {t("app.signOut")}
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}
