import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Menu, Text, VisuallyHidden } from "@mantine/core";
import { IconCheck, IconChevronDown, IconStack2 } from "@tabler/icons-react";
import { useSession } from "@/auth/session-context";
import { UnitMark } from "@/design/patterns";
import { offersWorkingUnits, placeChoices, type WorkingUnitState } from "./working-unit";

const indentStep = 16;

/**
 * The header's choice of the unit the person works for (TF-222, ADR-0038): only with more than one unit to choose
 * (ADR-0033). The button shows the unit's mark and name; the menu lists the units on the tree, indented under the ones
 * above them, with the path written under a unit whose parent is not a choice, and "All my units" where a role reaches
 * several at once. The current choice has a tick and says so in words.
 */
export function WorkingUnitControl({ choice }: { choice: WorkingUnitState }) {
  const { t } = useTranslation();
  const { me } = useSession();
  const options = me?.working_units;
  if (!me || !options || !offersWorkingUnits(me)) {
    return null;
  }

  const all = me.all_units === true;
  const current = all ? undefined : me.working_unit;
  const currentCode = current ? options.units.find((unit) => unit.id === current.id)?.code : undefined;
  const name = all ? t("workingUnit.all") : (current?.name ?? "");

  return (
    // Without the placeholder Mantine puts first for focus: a menu holds menu items only, and the first item takes focus.
    <Menu position="bottom-end" withinPortal withInitialFocusPlaceholder={false}>
      <Menu.Target>
        <Button
          variant="subtle"
          size="xs"
          loading={choice.busy}
          leftSection={
            currentCode !== undefined ? (
              <UnitMark code={currentCode} size={18} />
            ) : (
              <IconStack2 size={16} aria-hidden="true" />
            )
          }
          rightSection={<IconChevronDown size={14} aria-hidden="true" />}
          aria-label={t("workingUnit.control", { name })}
          data-working-unit={all ? "all" : current?.id}
        >
          <Text span size="sm" visibleFrom="sm" maw={200} truncate>
            {name}
          </Text>
        </Button>
      </Menu.Target>
      <Menu.Dropdown miw={260} mah={420} style={{ overflowY: "auto" }} aria-label={t("workingUnit.title")}>
        {placeChoices(options.units).map(({ unit, indent, above }) => {
          const chosen = !all && current?.id === unit.id;
          return (
            <Menu.Item
              key={unit.id}
              onClick={() => void choice.choose({ unitId: unit.id })}
              leftSection={
                <Box pl={indent * indentStep} style={{ display: "inline-flex" }}>
                  <UnitMark code={unit.code} size={18} />
                </Box>
              }
              rightSection={chosen ? <IconCheck size={16} aria-hidden="true" /> : undefined}
              data-current={chosen ? "true" : undefined}
            >
              <Text span size="sm" display="block">
                {unit.name}
                {chosen && <VisuallyHidden component="span">{t("workingUnit.current")}</VisuallyHidden>}
              </Text>
              {above.length > 0 && (
                <Text span size="xs" c="dimmed" display="block">
                  {t("workingUnit.under", { path: above.join(" / ") })}
                </Text>
              )}
            </Menu.Item>
          );
        })}
        {options.all_units_available && (
          <Menu.Item
            onClick={() => void choice.choose({ allUnits: true })}
            leftSection={<IconStack2 size={18} aria-hidden="true" />}
            rightSection={all ? <IconCheck size={16} aria-hidden="true" /> : undefined}
            data-current={all ? "true" : undefined}
          >
            <Text span size="sm" display="block">
              {t("workingUnit.all")}
              {all && <VisuallyHidden component="span">{t("workingUnit.current")}</VisuallyHidden>}
            </Text>
            <Text span size="xs" c="dimmed" display="block">
              {t("workingUnit.allHint")}
            </Text>
          </Menu.Item>
        )}
      </Menu.Dropdown>
    </Menu>
  );
}

/** Why the last choice of unit failed, above the page, with the correlation id to quote. */
export function WorkingUnitFailure({ choice }: { choice: WorkingUnitState }) {
  const { t } = useTranslation();
  if (!choice.failure) {
    return null;
  }
  return (
    <Alert
      role="alert"
      color="danger"
      mb="md"
      withCloseButton
      closeButtonLabel={t("dialog.close")}
      onClose={choice.clearFailure}
    >
      <Text size="sm">{choice.failure.error?.message ?? t("errors.unexpected")}</Text>
      {choice.failure.error?.correlationId !== undefined && (
        <Text size="xs" c="dimmed" mt={4}>
          {t("errors.correlation", { id: choice.failure.error.correlationId })}
        </Text>
      )}
    </Alert>
  );
}
