import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import {
  Alert,
  Anchor,
  Burger,
  Button,
  Checkbox,
  Code,
  Group,
  Loader,
  Menu,
  NavLink,
  Paper,
  PasswordInput,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
  Title,
} from "@mantine/core";
import { notifications } from "@mantine/notifications";
import { IconAlertTriangle, IconCheck, IconChevronDown, IconInfoCircle, IconX } from "@tabler/icons-react";

function Example({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Stack gap="xs">
      <Title order={3}>{title}</Title>
      {children}
    </Stack>
  );
}

/** Every Mantine component the shell uses, in its themed form. */
export function ComponentsSection() {
  const { t } = useTranslation();
  const [burgerOpen, setBurgerOpen] = useState(false);
  const alertText = t("gallery.components.alertText");

  return (
    <section aria-labelledby="gallery-components">
      <Stack gap="lg">
        <Title order={2} id="gallery-components">
          {t("gallery.sections.components")}
        </Title>
        <Example title={t("gallery.components.buttons")}>
          <Group>
            <Button>{t("gallery.components.primary")}</Button>
            <Button variant="default">{t("gallery.components.default")}</Button>
            <Button variant="light">{t("gallery.components.light")}</Button>
            <Button variant="subtle">{t("gallery.components.subtle")}</Button>
            <Button disabled>{t("gallery.components.disabled")}</Button>
            <Button loading>{t("gallery.components.loading")}</Button>
          </Group>
        </Example>
        <Example title={t("gallery.components.inputs")}>
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <TextInput
              label={t("gallery.components.textLabel")}
              description={t("gallery.components.textDescription")}
            />
            <TextInput label={t("gallery.components.textLabel")} error={t("gallery.components.textError")} />
            <PasswordInput label={t("gallery.components.passwordLabel")} />
            <Checkbox label={t("gallery.components.checkbox")} defaultChecked />
          </SimpleGrid>
        </Example>
        <Example title={t("gallery.components.alerts")}>
          <Alert color="info" title={t("gallery.components.alertInfo")} icon={<IconInfoCircle size={18} />}>
            {alertText}
          </Alert>
          <Alert color="success" title={t("gallery.components.alertSuccess")} icon={<IconCheck size={18} />}>
            {alertText}
          </Alert>
          <Alert color="warning" title={t("gallery.components.alertWarning")} icon={<IconAlertTriangle size={18} />}>
            {alertText}
          </Alert>
          <Alert color="danger" title={t("gallery.components.alertDanger")} icon={<IconX size={18} />}>
            {alertText}
          </Alert>
        </Example>
        <Example title={t("gallery.components.menu")}>
          <Menu position="bottom-start">
            <Menu.Target>
              <Button variant="default" rightSection={<IconChevronDown size={14} />}>
                {t("gallery.components.open")}
              </Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item>{t("gallery.components.menuItem")}</Menu.Item>
              <Menu.Item>{t("gallery.components.menuItemTwo")}</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </Example>
        <Example title={t("gallery.components.navigation")}>
          <Paper withBorder w={240} p="sm">
            <NavLink label={t("gallery.components.navPlain")} href="#gallery-components" />
            <NavLink label={t("gallery.components.navActive")} href="#gallery-components" active />
            <NavLink label={t("gallery.components.navNested")} childrenOffset={12} defaultOpened>
              <NavLink label={t("gallery.components.navChild")} href="#gallery-components" />
            </NavLink>
          </Paper>
        </Example>
        <Example title={t("gallery.components.paper")}>
          <Paper withBorder p="lg">
            <Text>{t("gallery.components.paperText")}</Text>
          </Paper>
        </Example>
        <Example title={t("gallery.components.text")}>
          <Text>{t("gallery.components.textBody")}</Text>
          <Text c="dimmed" size="sm">
            {t("gallery.components.textDimmed")}
          </Text>
        </Example>
        <Example title={t("gallery.components.code")}>
          <Code>{t("gallery.components.codeSample")}</Code>
        </Example>
        <Example title={t("gallery.components.loader")}>
          <Loader />
        </Example>
        <Example title={t("gallery.components.burger")}>
          <Burger
            opened={burgerOpen}
            onClick={() => setBurgerOpen((open) => !open)}
            aria-label={t("app.toggleNavigation")}
          />
        </Example>
        <Example title={t("gallery.components.anchor")}>
          <Text>
            {t("gallery.components.anchorLead")}{" "}
            <Anchor href="#gallery-colour">{t("gallery.components.anchorText")}</Anchor>
          </Text>
        </Example>
        <Example title={t("gallery.components.notification")}>
          <Group>
            <Button
              variant="light"
              onClick={() =>
                notifications.show({
                  title: t("gallery.components.notifyTitle"),
                  message: t("gallery.components.notifyText"),
                })
              }
            >
              {t("gallery.components.notify")}
            </Button>
          </Group>
        </Example>
      </Stack>
    </section>
  );
}
