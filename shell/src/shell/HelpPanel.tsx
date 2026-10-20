import { useTranslation } from "react-i18next";
import { useLocation } from "react-router";
import { ActionIcon, Drawer, Stack, Text, Title, Tooltip } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconHelp } from "@tabler/icons-react";
import { ShortcutList } from "@/components/ShortcutList";
import { shellShortcuts } from "@/components/shortcuts";
import { manifests } from "@/modules";
import { routeAt } from "@/modules/registry";

/**
 * Help (TF-170, ADR-0033): a panel from the right edge with the shell's keys, the current page's keys from its manifest,
 * and where the documentation will live. The panel is a Mantine drawer: Escape closes it and focus returns to the button.
 */
export function HelpButton() {
  const { t } = useTranslation();
  const [opened, { open, close }] = useDisclosure(false);
  const { pathname } = useLocation();
  const route = pathname === "/" ? undefined : routeAt(manifests, pathname);
  const page = route?.shortcuts ?? [];
  return (
    <>
      <Tooltip label={t("help.open")} withinPortal>
        <ActionIcon variant="default" size={36} onClick={open} aria-label={t("help.open")}>
          <IconHelp size={18} aria-hidden="true" />
        </ActionIcon>
      </Tooltip>
      <Drawer
        opened={opened}
        onClose={close}
        position="right"
        title={t("help.title")}
        closeButtonProps={{ "aria-label": t("dialog.close") }}
      >
        <Stack gap="lg">
          <Stack gap="xs">
            <Title order={3} size="h5">
              {t("help.shell")}
            </Title>
            <ShortcutList shortcuts={shellShortcuts} label={t("help.shell")} />
          </Stack>
          {page.length > 0 && route && (
            <Stack gap="xs">
              <Title order={3} size="h5">
                {t("help.page", { page: t(route.titleKey) })}
              </Title>
              <ShortcutList shortcuts={page} label={t("help.page", { page: t(route.titleKey) })} />
            </Stack>
          )}
          <Text size="sm" c="dimmed">
            {t("help.documentation")}
          </Text>
        </Stack>
      </Drawer>
    </>
  );
}
