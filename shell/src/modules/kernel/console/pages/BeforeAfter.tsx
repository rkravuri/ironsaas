import { useTranslation } from "react-i18next";
import { Badge, Table, Text } from "@mantine/core";
import { changeRows, valueWords } from "./change-words";

/**
 * A change's state before and after, side by side (TF-068, ADR-0052): one row per field, the fields that change marked
 * in words, so the approver sees exactly what they approve. Nothing before means it is made; nothing after, removed.
 */
export function BeforeAfter({ before, after }: { before: unknown; after: unknown }) {
  const { t } = useTranslation();
  const rows = changeRows(before, after);
  return (
    <Table.ScrollContainer minWidth={560}>
      <Table withTableBorder withColumnBorders layout="fixed" data-before-after>
        <Table.Caption>{t("changes.record.compareCaption")}</Table.Caption>
        <Table.Thead>
          <Table.Tr>
            <Table.Th w="24%">{t("changes.record.field")}</Table.Th>
            <Table.Th>{before === null ? t("changes.record.beforeNothing") : t("changes.record.before")}</Table.Th>
            <Table.Th>{after === null ? t("changes.record.afterNothing") : t("changes.record.after")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rows.map((row) => (
            <Table.Tr key={row.field} data-field={row.field} data-changed={row.changed}>
              <Table.Td>
                <Text size="sm" ff="monospace" component="span">
                  {row.field}
                </Text>
                {row.changed && (
                  <Badge variant="light" color="info" size="sm" radius="sm" ml="xs" tt="none">
                    {t("changes.record.changed")}
                  </Badge>
                )}
              </Table.Td>
              <Table.Td style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>
                {before === null ? "" : valueWords(row.before, t)}
              </Table.Td>
              <Table.Td
                style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}
                {...(row.changed ? { fw: 600 } : {})}
              >
                {after === null ? "" : valueWords(row.after, t)}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
