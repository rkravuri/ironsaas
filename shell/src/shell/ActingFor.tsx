import { useTranslation } from "react-i18next";
import { Alert, Button, Group, Menu, Stack, Text, VisuallyHidden } from "@mantine/core";
import { IconCheck, IconChevronDown, IconUserShare } from "@tabler/icons-react";
import { useSession } from "@/auth/session-context";
import { dutyList, type ActingChoice } from "./acting";

const until = (iso: string, language: string) => new Date(iso).toLocaleString(language, { dateStyle: "medium", timeStyle: "short" });

/**
 * The header's "Acting for" control (TF-037, ADR-0037): only while the person holds an active delegation, or still acts
 * with one, since otherwise it could do nothing (ADR-0033). It offers the person themselves and each delegator, with the
 * duties and until when, and marks the current choice with a tick and in words.
 */
export function ActingForControl({ choice }: { choice: ActingChoice }) {
  const { t, i18n } = useTranslation();
  const { me } = useSession();
  const received = me?.delegations?.received ?? [];
  const acting = me?.acting_for ?? null;
  if (!me || (received.length === 0 && acting === null)) {
    return null;
  }

  return (
    // Without the placeholder Mantine puts first for focus: a menu holds menu items only, and the first item takes focus.
    <Menu position="bottom-end" withinPortal withInitialFocusPlaceholder={false}>
      <Menu.Target>
        <Button
          variant={acting ? "light" : "subtle"}
          size="xs"
          loading={choice.busy}
          leftSection={<IconUserShare size={16} aria-hidden="true" />}
          rightSection={<IconChevronDown size={14} aria-hidden="true" />}
          aria-label={acting ? t("acting.control.actingFor", { name: acting.name }) : t("acting.control.label")}
          data-acting={acting ? "true" : undefined}
        >
          <Text span size="sm" visibleFrom="sm">
            {acting ? t("acting.control.short", { name: acting.name }) : t("acting.control.open")}
          </Text>
        </Button>
      </Menu.Target>
      {/* Only items in the menu: a label among them is not a menu item, and assistive technology reads the button's name. */}
      <Menu.Dropdown miw={280} aria-label={t("acting.control.title")}>
        <Menu.Item onClick={() => void choice.choose(null)} rightSection={acting === null ? <IconCheck size={16} aria-hidden="true" /> : undefined} data-current={acting === null ? "true" : undefined}>
          {t("acting.control.myself")}
          {acting === null && <VisuallyHidden>{t("acting.control.current")}</VisuallyHidden>}
        </Menu.Item>
        {received.map((delegation) => {
          const current = acting?.id === delegation.delegator_id;
          return (
            <Menu.Item
              key={delegation.delegator_id}
              onClick={() => void choice.choose(delegation.delegator_id)}
              rightSection={current ? <IconCheck size={16} aria-hidden="true" /> : undefined}
              data-current={current ? "true" : undefined}
            >
              <Text span size="sm" display="block">
                {t("acting.control.for", { name: delegation.delegator_name })}
                {current && <VisuallyHidden>{t("acting.control.current")}</VisuallyHidden>}
              </Text>
              <Text span size="xs" c="dimmed" display="block">
                {t("acting.control.duties", { duties: dutyList(delegation.duties, i18n.language), until: until(delegation.until, i18n.language) })}
              </Text>
            </Menu.Item>
          );
        })}
      </Menu.Dropdown>
    </Menu>
  );
}

/**
 * The calm band under the header while the person acts for someone (ADR-0037): whose rights, which duties, until when,
 * and how to stop. It sits in the page's flow, so it never covers the work. The same place says when the delegation
 * ended and the person acts as themselves again, and why a choice failed.
 */
export function ActingBand({ choice }: { choice: ActingChoice }) {
  const { t, i18n } = useTranslation();
  const { me, actingEnded, dismissActingEnded } = useSession();
  const acting = me?.acting_for ?? null;

  return (
    <>
      {acting && (
        <Alert
          role="status"
          variant="light"
          color="info"
          mb="md"
          icon={<IconUserShare size={18} aria-hidden="true" />}
          title={t("acting.band.title", { name: acting.name })}
          data-acting-band
        >
          <Group justify="space-between" align="flex-end" gap="sm">
            <Stack gap={2} maw={720}>
              <Text size="sm">{t("acting.band.rights", { name: acting.name, duties: dutyList(acting.duties, i18n.language), until: until(acting.until, i18n.language) })}</Text>
              <Text size="sm">{t("acting.band.both")}</Text>
            </Stack>
            <Button variant="default" size="xs" loading={choice.busy} onClick={() => void choice.choose(null)}>
              {t("acting.band.stop")}
            </Button>
          </Group>
        </Alert>
      )}
      {actingEnded !== undefined && !acting && (
        <Alert role="status" variant="light" color="info" mb="md" withCloseButton closeButtonLabel={t("acting.ended.dismiss")} onClose={dismissActingEnded} data-acting-ended>
          <Text size="sm">{actingEnded ? t("acting.ended.named", { name: actingEnded }) : t("acting.ended.unnamed")}</Text>
        </Alert>
      )}
      {choice.failure && (
        <Alert role="alert" color="danger" mb="md" withCloseButton closeButtonLabel={t("dialog.close")} onClose={choice.clearFailure}>
          <Text size="sm">{choice.failure.error?.message ?? t("errors.unexpected")}</Text>
          {choice.failure.error?.correlationId !== undefined && (
            <Text size="xs" c="dimmed" mt={4}>
              {t("errors.correlation", { id: choice.failure.error.correlationId })}
            </Text>
          )}
        </Alert>
      )}
    </>
  );
}
