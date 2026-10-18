import { useTranslation } from "react-i18next";
import { Stack, Text } from "@mantine/core";
import type { INoRowsOverlayParams } from "ag-grid-community";
import { NothingFound, NothingYet } from "@/design/illustrations";

/** The grid's empty state: nothing yet, or nothing that matches the filter the viewer set (brief section 3). */
export function NoRowsOverlay({ api }: INoRowsOverlayParams) {
  const { t } = useTranslation();
  const filtered = api.isAnyFilterPresent();
  return (
    <Stack align="center" gap="xs" role="status">
      {filtered ? <NothingFound decorative size="sm" /> : <NothingYet decorative size="sm" />}
      <Text c="dimmed" size="sm">
        {t(filtered ? "grid.emptyFiltered" : "grid.empty")}
      </Text>
    </Stack>
  );
}
