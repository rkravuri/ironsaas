import { useTranslation } from "react-i18next";
import { Anchor, Text } from "@mantine/core";

/**
 * The line under every list of session places: they come from DB-IP's IP to City Lite, whose licence (CC BY 4.0) asks
 * for a link back wherever its data shows (ADR-0049, docs/licences.md).
 */
export function PlaceSource() {
  const { t } = useTranslation();
  return (
    <Text size="xs" c="dimmed">
      {t("sessions.placeSource.text")}{" "}
      <Anchor href="https://db-ip.com" size="xs">
        {t("sessions.placeSource.link")}
      </Anchor>
    </Text>
  );
}
